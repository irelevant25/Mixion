using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Mixion.Host.Interop;
using static Mixion.Host.Audio.LowLatencyComInterop;

namespace Mixion.Host.Audio;

/// <summary>
/// WASAPI shared-mode render using <c>IAudioClient3::InitializeSharedAudioStream</c>
/// — counterpart to <see cref="LowLatencyCaptureDevice"/> on the output
/// side. Saves ~7 ms of one-way render latency vs. the regular
/// <see cref="RenderDevice"/> on supported hardware.
///
/// Same external shape as <see cref="RenderDevice"/>: callers (the mix
/// engine) write interleaved stereo float pairs into <see cref="Ring"/>,
/// the render thread drains the ring on every WASAPI event signal and
/// writes into <c>IAudioRenderClient</c>'s buffer with the device's
/// native channel layout.
///
/// The constructor probes for low-latency support and throws if any step
/// fails; <see cref="EngineFactory"/> falls back to <see cref="RenderDevice"/>.
/// </summary>
public sealed class LowLatencyRenderDevice : IRenderDevice
{
    private readonly string     _id;
    private readonly string     _friendlyName;
    private readonly WaveFormat _format;
    private readonly RingBuffer _ring;
    private readonly float[]    _scratch;
    // Per-tick float buffer sized to one device buffer × channels. Not
    // readonly — Array.Resize on the off chance _bufferFrames is bigger
    // than the construction-time guess (rare; the engine period typically
    // pins this at hundreds of frames, well under the initial allocation).
    private float[]             _deviceScratch;
    private readonly int        _latencyMs;

    private IAudioClient3?      _audioClient;
    private IAudioRenderClient? _renderClient;
    private IntPtr              _eventHandle = IntPtr.Zero;
    private uint                _bufferFrames;
    private Thread?             _renderThread;
    private IntPtr              _mmcssHandle;
    private volatile bool       _running;

    public string     Id           => _id;
    public string     FriendlyName => _friendlyName;
    public int        SampleRate   => _format.SampleRate;
    public int        DestChannels => _format.Channels;
    public int        BitsPerSample => _format.BitsPerSample;
    public RingBuffer Ring         => _ring;
    public int        LatencyMs    => _latencyMs;

    public LowLatencyRenderDevice(MMDevice device, int ringCapacityFrames)
    {
        _id           = device.ID;
        _friendlyName = device.FriendlyName;
        _format       = device.AudioClient.MixFormat;
        // Ring holds interleaved stereo floats (the mix engine's wire format).
        _ring         = new RingBuffer(ringCapacityFrames * 2);
        _scratch      = new float[8192];
        // Per-tick scratch sized for one full device buffer at native channel
        // count. Render loop fills this then bulk-Marshals it into WASAPI.
        _deviceScratch = new float[ringCapacityFrames * Math.Max(1, _format.Channels)];

        if (!WaveFormatX.IsFloat(_format) || _format.BitsPerSample != 32)
            throw new NotSupportedException(
                "Low-latency render requires the device's mix format to be 32-bit IEEE float. " +
                $"Got {WaveFormatX.Describe(_format)}.");

        var latencyHolder = 0;
        RunOnMta(() => latencyHolder = ActivateAndInitialize());
        _latencyMs = latencyHolder;
    }

    private int ActivateAndInitialize()
    {
        var enumIid = IID_IMMDeviceEnumerator;
        var clsid   = CLSID_MMDeviceEnumerator;
        var hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref enumIid, out var enumObj);
        if (hr < 0 || enumObj is null)
            throw new InvalidOperationException($"CoCreateInstance(MMDeviceEnumerator) failed. HRESULT 0x{hr:X8}.");
        var enumerator = (IMMDeviceEnumerator)enumObj;

        try
        {
            hr = enumerator.GetDevice(_id, out var mmDev);
            if (hr < 0 || mmDev is null)
                throw new InvalidOperationException(
                    $"IMMDeviceEnumerator::GetDevice failed for '{_id}'. HRESULT 0x{hr:X8}.");

            try
            {
                var clientIid = IID_IAudioClient3;
                hr = mmDev.Activate(ref clientIid, CLSCTX_INPROC_SERVER, IntPtr.Zero, out var clientObj);
                if (hr < 0 || clientObj is null)
                    throw new NotSupportedException(
                        $"IMMDevice::Activate(IAudioClient3) failed. HRESULT 0x{hr:X8}. " +
                        "Requires Windows 10 1803+ and a driver that supports IAudioClient3.");
                _audioClient = (IAudioClient3)clientObj;
            }
            finally
            {
                Marshal.ReleaseComObject(mmDev);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }

        var formatBytes  = WaveFormatToBytes(_format);
        var formatHandle = GCHandle.Alloc(formatBytes, GCHandleType.Pinned);
        try
        {
            var formatPtr = formatHandle.AddrOfPinnedObject();

            hr = _audioClient.GetSharedModeEnginePeriod(
                formatPtr,
                out var defaultPeriod,
                out var _,
                out var minPeriod,
                out var _);
            if (hr < 0)
                throw new NotSupportedException(
                    $"IAudioClient3::GetSharedModeEnginePeriod failed. HRESULT 0x{hr:X8}.");

            var period = minPeriod > 0 ? minPeriod : defaultPeriod;
            if (period == 0)
                throw new NotSupportedException("IAudioClient3 reported a zero engine period; cannot initialize.");

            hr = _audioClient.InitializeSharedAudioStream(
                AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
                period,
                formatPtr,
                IntPtr.Zero);
            if (hr < 0)
                throw new NotSupportedException(
                    $"IAudioClient3::InitializeSharedAudioStream failed at period {period} frames. HRESULT 0x{hr:X8}.");

            hr = _audioClient.GetBufferSize(out _bufferFrames);
            if (hr < 0)
                throw new InvalidOperationException($"IAudioClient3::GetBufferSize failed. HRESULT 0x{hr:X8}.");

            _eventHandle = CreateEventW(IntPtr.Zero, false, false, null);
            if (_eventHandle == IntPtr.Zero)
                throw new InvalidOperationException("Could not create event handle for low-latency render.");

            hr = _audioClient.SetEventHandle(_eventHandle);
            if (hr < 0)
                throw new InvalidOperationException($"IAudioClient3::SetEventHandle failed. HRESULT 0x{hr:X8}.");

            var renderIid = IID_IAudioRenderClient;
            hr = _audioClient.GetService(ref renderIid, out var renderService);
            if (hr < 0 || renderService is null)
                throw new InvalidOperationException(
                    $"IAudioClient3::GetService(IAudioRenderClient) failed. HRESULT 0x{hr:X8}.");
            _renderClient = (IAudioRenderClient)renderService;

            // Pre-fill with silence so Start has something to play on the
            // first device tick — without this the engine briefly underruns
            // and some drivers report glitches in the WASAPI session log.
            hr = _renderClient.GetBuffer(_bufferFrames, out var primePtr);
            if (hr >= 0 && primePtr != IntPtr.Zero)
            {
                var primeFloats = (int)_bufferFrames * _format.Channels;
                if (primeFloats > _deviceScratch.Length)
                    Array.Resize(ref _deviceScratch, primeFloats);
                Array.Clear(_deviceScratch, 0, primeFloats);
                Marshal.Copy(_deviceScratch, 0, primePtr, primeFloats);
                _renderClient.ReleaseBuffer(_bufferFrames, 0);
            }

            var latencyMs = (int)Math.Ceiling(period * 1000.0 / _format.SampleRate);
            return Math.Max(1, latencyMs);
        }
        finally
        {
            formatHandle.Free();
        }
    }

    public void Start()
    {
        if (_running) return;
        if (_audioClient is null) throw new InvalidOperationException("Render not activated.");

        RunOnMta(() => _audioClient!.Start());
        _running = true;
        _mmcssHandle = Mmcss.Begin();

        _renderThread = new Thread(RenderLoop)
        {
            IsBackground = true,
            Name         = $"LowLatencyRender({_friendlyName})",
            Priority     = ThreadPriority.Highest,
        };
        _renderThread.SetApartmentState(ApartmentState.MTA);
        _renderThread.Start();
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;

        RunOnMta(() => { try { _audioClient?.Stop(); } catch { /* device may have vanished */ } });

        if (_eventHandle != IntPtr.Zero) SetEvent(_eventHandle);
        _renderThread?.Join(TimeSpan.FromSeconds(2));
        _renderThread = null;

        Mmcss.Revert(_mmcssHandle);
        _mmcssHandle = IntPtr.Zero;
    }

    public void Dispose()
    {
        Stop();

        RunOnMta(() =>
        {
            if (_renderClient is not null)
            {
                Marshal.ReleaseComObject(_renderClient);
                _renderClient = null;
            }
            if (_audioClient is not null)
            {
                Marshal.ReleaseComObject(_audioClient);
                _audioClient = null;
            }
        });

        if (_eventHandle != IntPtr.Zero)
        {
            CloseHandle(_eventHandle);
            _eventHandle = IntPtr.Zero;
        }
    }

    private void RenderLoop()
    {
        const uint waitMs = 100;
        var channels = _format.Channels;

        while (_running)
        {
            var rc = WaitForSingleObject(_eventHandle, waitMs);
            if (!_running) break;
            if (rc != 0 && rc != 0x102) break;

            if (_audioClient is null || _renderClient is null) return;

            // Ask how much room is in the device's buffer right now.
            if (_audioClient.GetCurrentPadding(out var padding) < 0) continue;
            var framesAvailable = _bufferFrames - padding;
            if (framesAvailable == 0) continue;

            if (_renderClient.GetBuffer(framesAvailable, out var bufferPtr) < 0) continue;
            if (bufferPtr == IntPtr.Zero) { _renderClient.ReleaseBuffer(0, 0); continue; }

            var totalFloats = (int)framesAvailable * channels;
            if (totalFloats > _deviceScratch.Length)
                Array.Resize(ref _deviceScratch, totalFloats);
            FillFromRing(_deviceScratch.AsSpan(0, totalFloats), (int)framesAvailable);
            Marshal.Copy(_deviceScratch, 0, bufferPtr, totalFloats);

            _renderClient.ReleaseBuffer(framesAvailable, 0);
        }
    }

    /// <summary>
    /// Pull interleaved stereo (L,R) pairs from the ring into the device's
    /// native channel layout. Mirror of
    /// <c>RingBufferWaveProvider.Read</c> in <see cref="RenderDevice"/>:
    /// mono devices get the L/R average, stereo gets straight L/R, multi
    /// channel gets L on 0, R on 1, average on the rest. Underrun → silent
    /// tail rather than stale audio.
    /// </summary>
    private void FillFromRing(Span<float> dst, int frames)
    {
        var channels = _format.Channels;
        var maxFloats  = _scratch.Length & ~1; // even count — two floats per stereo frame
        var needFloats = Math.Min(frames * 2, maxFloats);
        var read       = _ring.Read(_scratch.AsSpan(0, needFloats));
        if (read < needFloats)
            _scratch.AsSpan(read, needFloats - read).Clear();

        var stereoFrames = needFloats / 2;

        if (channels == 1)
        {
            for (var i = 0; i < stereoFrames; i++)
            {
                var L = _scratch[i * 2];
                var R = _scratch[i * 2 + 1];
                dst[i] = (L + R) * 0.5f;
            }
        }
        else
        {
            for (var i = 0; i < stereoFrames; i++)
            {
                var L   = _scratch[i * 2];
                var R   = _scratch[i * 2 + 1];
                var off = i * channels;
                dst[off]     = L;
                dst[off + 1] = R;
                if (channels > 2)
                {
                    var mid = (L + R) * 0.5f;
                    for (var c = 2; c < channels; c++) dst[off + c] = mid;
                }
            }
        }

        if (stereoFrames < frames)
            dst.Slice(stereoFrames * channels).Clear();
    }

}
