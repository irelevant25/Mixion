using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Mixion.Host.Interop;
using static Mixion.Host.Audio.LowLatencyComInterop;

namespace Mixion.Host.Audio;

/// <summary>
/// WASAPI shared-mode capture using <c>IAudioClient3::InitializeSharedAudioStream</c>
/// — the OS audio engine runs at its minimum supported period (typically
/// 3 ms on Windows 10 1803+ with low-latency-capable drivers) instead of
/// the regular 10 ms shared-mode period. Saves ~7 ms of one-way capture
/// latency vs. <see cref="CaptureDevice"/> on supported hardware.
///
/// The constructor probes for low-latency support and throws if any step
/// fails (older OS, driver doesn't support the min period, format
/// rejected). <see cref="EngineFactory"/> catches and falls back to the
/// regular <see cref="CaptureDevice"/> on a best-effort basis.
///
/// Threading: COM activation, Initialize, and Start all run on an MTA
/// bridge thread (host entry is STA). The capture worker is created MTA
/// so its event-driven reads from <c>IAudioCaptureClient</c> don't cross
/// apartments.
/// </summary>
public sealed class LowLatencyCaptureDevice : IAudioCaptureSource
{
    private readonly string         _id;
    private readonly string         _friendlyName;
    private readonly WaveFormat     _format;
    private readonly RingBuffer     _ring;
    private readonly float[]        _scratchMono;
    private readonly byte[]         _scratchBytes;
    private readonly int            _bufferMs;

    private IAudioClient3?       _audioClient;
    private IAudioCaptureClient? _captureClient;
    private IntPtr               _eventHandle = IntPtr.Zero;
    private Thread?              _captureThread;
    private IntPtr               _mmcssHandle;
    private volatile bool        _running;

    public string     Id           => _id;
    public string     FriendlyName => _friendlyName;
    public int        SampleRate   => _format.SampleRate;
    public int        SourceChannels => _format.Channels;
    public int        BitsPerSample  => _format.BitsPerSample;
    public RingBuffer Ring         => _ring;
    public int        BufferMilliseconds => _bufferMs;
    public event EventHandler? DataReady;

    /// <summary>
    /// Open <paramref name="device"/> in low-latency shared mode.
    /// Throws on any unsupported state — caller is expected to fall back
    /// to <see cref="CaptureDevice"/>.
    /// </summary>
    public LowLatencyCaptureDevice(MMDevice device, int ringCapacitySamples)
    {
        _id           = device.ID;
        _friendlyName = device.FriendlyName;
        _format       = device.AudioClient.MixFormat;
        _ring         = new RingBuffer(ringCapacitySamples);
        _scratchMono  = new float[ringCapacitySamples];
        _scratchBytes = new byte[ringCapacitySamples * Math.Max(1, _format.Channels) * (_format.BitsPerSample / 8)];

        var bufferMsHolder = 0;
        RunOnMta(() => bufferMsHolder = ActivateAndInitialize());
        _bufferMs = bufferMsHolder;
    }

    /// <summary>
    /// Activate the device's audio client, ask it for its low-latency engine
    /// period, and initialize the shared stream at that period. Returns the
    /// resulting buffer duration in ms — surfaced via
    /// <see cref="BufferMilliseconds"/> so the latency estimator can show
    /// the value the OS actually granted.
    /// </summary>
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
                    $"IAudioClient3::GetSharedModeEnginePeriod failed. HRESULT 0x{hr:X8}. " +
                    "Driver does not advertise a low-latency engine period.");

            // Use the minimum period when available; if it's zero (rare) fall
            // back to the default period to keep us in low-latency mode rather
            // than passing 0 (which the OS may treat as "use default").
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

            _eventHandle = CreateEventW(IntPtr.Zero, false, false, null);
            if (_eventHandle == IntPtr.Zero)
                throw new InvalidOperationException("Could not create event handle for low-latency capture.");

            hr = _audioClient.SetEventHandle(_eventHandle);
            if (hr < 0)
                throw new InvalidOperationException($"IAudioClient3::SetEventHandle failed. HRESULT 0x{hr:X8}.");

            var captureIid = IID_IAudioCaptureClient;
            hr = _audioClient.GetService(ref captureIid, out var captureService);
            if (hr < 0 || captureService is null)
                throw new InvalidOperationException(
                    $"IAudioClient3::GetService(IAudioCaptureClient) failed. HRESULT 0x{hr:X8}.");
            _captureClient = (IAudioCaptureClient)captureService;

            var bufferMs = (int)Math.Ceiling(period * 1000.0 / _format.SampleRate);
            return Math.Max(1, bufferMs);
        }
        finally
        {
            formatHandle.Free();
        }
    }

    public void Start()
    {
        if (_running) return;
        if (_audioClient is null) throw new InvalidOperationException("Capture not activated.");

        RunOnMta(() => _audioClient!.Start());
        _running = true;
        _mmcssHandle = Mmcss.Begin();

        _captureThread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Name         = $"LowLatencyCapture({_friendlyName})",
            Priority     = ThreadPriority.Highest,
        };
        _captureThread.SetApartmentState(ApartmentState.MTA);
        _captureThread.Start();
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;

        RunOnMta(() => { try { _audioClient?.Stop(); } catch { /* device may have vanished */ } });

        if (_eventHandle != IntPtr.Zero) SetEvent(_eventHandle);
        _captureThread?.Join(TimeSpan.FromSeconds(2));
        _captureThread = null;

        Mmcss.Revert(_mmcssHandle);
        _mmcssHandle = IntPtr.Zero;
    }

    public void Dispose()
    {
        Stop();

        RunOnMta(() =>
        {
            if (_captureClient is not null)
            {
                Marshal.ReleaseComObject(_captureClient);
                _captureClient = null;
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

    private void CaptureLoop()
    {
        const uint waitMs = 100;
        var bytesPerFrame = _format.Channels * (_format.BitsPerSample / 8);

        while (_running)
        {
            var rc = WaitForSingleObject(_eventHandle, waitMs);
            if (!_running) break;
            // 0 = signalled (data ready), 0x102 = timeout (silent device — keep polling).
            if (rc != 0 && rc != 0x102) break;

            while (true)
            {
                if (_captureClient is null) return;

                var hr = _captureClient.GetNextPacketSize(out var packetFrames);
                if (hr < 0 || packetFrames == 0) break;

                hr = _captureClient.GetBuffer(out var dataPtr, out var framesAvailable, out var _, out var _, out var _);
                if (hr < 0) break;

                try
                {
                    if (framesAvailable > 0)
                    {
                        var byteCount = (int)framesAvailable * bytesPerFrame;
                        if (byteCount > _scratchBytes.Length) byteCount = _scratchBytes.Length;
                        Marshal.Copy(dataPtr, _scratchBytes, 0, byteCount);
                        var written = WaveFormatX.ConvertToMono(
                            _scratchBytes.AsSpan(0, byteCount), _format, _scratchMono);
                        _ring.Write(_scratchMono.AsSpan(0, written));
                        DataReady?.Invoke(this, EventArgs.Empty);
                    }
                }
                finally
                {
                    _captureClient.ReleaseBuffer(framesAvailable);
                }
            }
        }
    }
}
