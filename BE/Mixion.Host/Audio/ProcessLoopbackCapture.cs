using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using NAudio.Wave;
using Mixion.Host.Interop;

namespace Mixion.Host.Audio;

/// <summary>
/// Captures audio from a single Windows app via the per-process WASAPI
/// loopback API (Windows 10 build 20348+ / Windows 11). The OS isolates that
/// app's render streams and delivers them independently of any virtual cable,
/// so the mixer can take Chrome / Spotify / OBS / a game without forcing the
/// user to repoint Windows audio output at a virtual cable first.
///
/// NAudio does not wrap this variant of WASAPI activation, so this class drives
/// the native call chain directly:
/// <list type="number">
///   <item><c>ActivateAudioInterfaceAsync</c> with <c>AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK</c> + the target PID in include-process-tree mode.</item>
///   <item>Wait for the completion handler, fetch the resulting <c>IAudioClient</c> COM pointer.</item>
///   <item><c>IAudioClient::Initialize</c> in shared loopback mode with a fixed 16-bit PCM stereo format at the engine sample rate (the OS resamples the source for us).</item>
///   <item>Spin a dedicated capture thread that waits on an event handle and drains <c>IAudioCaptureClient::GetBuffer</c> into the engine's stereo ring.</item>
/// </list>
///
/// Target choice: callers pass the app's <em>root</em> process
/// (<see cref="ProcessSnapshot.ResolveAppRoot"/>). Multi-process apps play
/// audio from a helper child — Chrome's audio service, for one — whose PID
/// changes whenever the app recycles it; include-tree mode on the root keeps
/// capturing across those recycles. When the root itself exits (the user
/// closed the app) <see cref="HasTargetExited"/> reports it and the device
/// watcher re-binds the channel to the next instance by name.
///
/// DRM-protected streams (some Spotify/Netflix configurations) refuse loopback
/// at the OS level — the same hard limit a virtual cable would hit. Builds older
/// than 20348 fail at <c>ActivateAudioInterfaceAsync</c>; callers treat any
/// failure as "process loopback unavailable" and skip.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProcessLoopbackCapture : IAudioCaptureSource
{
    /// <summary>Magic device path string the OS interprets as "process-loopback activation".</summary>
    private const string VirtualAudioDeviceProcessLoopback = "VAD\\Process_Loopback";

    private const uint  AudioClientStreamFlagsLoopback           = 0x00020000;
    private const uint  AudioClientStreamFlagsEventCallback      = 0x00040000;
    /// <summary>Lets the engine resample the source process's output into our requested format. Mandatory for process loopback — source apps write arbitrary formats.</summary>
    private const uint  AudioClientStreamFlagsAutoConvertPcm     = 0x80000000;
    /// <summary>Pairs with AutoConvertPcm to ask for the default-quality SRC instead of the cheap one.</summary>
    private const uint  AudioClientStreamFlagsSrcDefaultQuality  = 0x08000000;
    private const uint  AudioClientShareModeShared               = 0;
    private const ushort PropVariantTypeBlob                     = 65; // VT_BLOB
    private const uint  ActivationTypeProcessLoopback            = 1;
    /// <summary><c>PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE</c> — the target and every process it spawns.</summary>
    private const uint  ProcessLoopbackModeIncludeTree           = 0;
    /// <summary><c>AUDCLNT_BUFFERFLAGS_SILENT</c> — the packet is silence and its bytes are undefined.</summary>
    private const uint  BufferFlagsSilent                        = 0x2;
    private const uint  ProcessQueryLimitedInformation           = 0x1000;
    private const uint  Synchronize                              = 0x00100000;

    /// <summary>Capture buffer duration requested from WASAPI (hundred-nanoseconds). 20 ms — matches Microsoft's ApplicationLoopback reference sample.</summary>
    private const long BufferDurationHns = 200_000;

    private readonly int                _processId;
    private readonly RingBuffer         _ring;
    private readonly float[]            _scratch;
    private readonly byte[]             _scratchBytes;
    private readonly int                _sampleRate;
    private readonly WaveFormat         _format;
    private readonly SafeProcessHandle? _processHandle;

    private IAudioClient?        _audioClient;
    private IAudioCaptureClient? _captureClient;
    private IntPtr               _eventHandle = IntPtr.Zero;
    private Thread?              _captureThread;
    private volatile bool        _running;
    private volatile bool        _faulted;

    public string Id           { get; }
    public string FriendlyName { get; }
    /// <summary>Executable name without extension — the identity the channel is keyed by.</summary>
    public string ProcessName  { get; }
    public int    SampleRate   => _sampleRate;
    public int    SourceChannels => _format.Channels;
    public int    BitsPerSample  => _format.BitsPerSample;
    public RingBuffer Ring     => _ring;
    public int    BufferMilliseconds => (int)(BufferDurationHns / 10_000);
    // Process loopback uses a fixed buffer (BufferDurationHns); convert to
    // frames at the engine sample rate for MixEngine alignment.
    public int    BufferFrames => Math.Max(1, BufferMilliseconds * _sampleRate / 1000);
    public bool   IsFaulted    => _faulted;
    public event EventHandler? DataReady;

    /// <summary>Root process id this capture is bound to.</summary>
    public int TargetProcessId => _processId;

    public ProcessLoopbackCapture(int targetProcessId, string processName, int sampleRate, int ringCapacityFrames)
    {
        _processId   = targetProcessId;
        _sampleRate  = sampleRate;
        ProcessName  = processName;
        Id           = ProcessChannelId.For(processName);
        FriendlyName = ProcessChannelId.DisplayName(processName);

        // 16-bit PCM stereo at the engine rate. The OS resamples the source
        // process's actual format into this for us (via the AUTOCONVERTPCM
        // flag at Initialize time) — that's what makes the process-loopback
        // path tolerate apps with arbitrary internal formats. PCM 16 is the
        // shape Microsoft's reference sample uses; float worked for some
        // processes but tripped E_INVALIDARG on others.
        _format = new WaveFormat(sampleRate, bits: 16, channels: 2);

        // Interleaved stereo — 2 floats per frame. The byte scratch holds one
        // WASAPI packet between the native pointer and the float conversion.
        _ring         = new RingBuffer(ringCapacityFrames * 2);
        _scratch      = new float[ringCapacityFrames * 2];
        _scratchBytes = new byte[ringCapacityFrames * _format.BlockAlign];

        // Keep a handle to the target so exit detection is exact — a handle
        // refers to this process object, so a recycled PID can't fool it.
        // Elevated targets may refuse; HasTargetExited then returns null and
        // the watcher falls back to a process snapshot.
        var handle = NativeMethods.OpenProcess(ProcessQueryLimitedInformation | Synchronize, false, (uint)targetProcessId);
        if (handle.IsInvalid) handle.Dispose();
        else _processHandle = handle;

        try
        {
            Activate();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// True when the target process has exited, false while it runs, or null
    /// when no handle to the target could be opened.
    /// </summary>
    public bool? HasTargetExited()
    {
        if (_processHandle is null) return null;
        try
        {
            return NativeMethods.WaitForSingleObject(_processHandle, 0) == 0;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    /// <summary>
    /// Drive the async activation synchronously. The completion handler signals
    /// a wait handle; we block on it for up to 5 s. Anything longer means the
    /// OS isn't going to deliver — usually wrong Windows build or a process
    /// that can't be looped back.
    ///
    /// All WASAPI COM work is forced onto an MTA thread via
    /// <see cref="RunOnMta"/>: the host's entry thread is <c>[STAThread]</c>
    /// (WinForms tray pump), and our capture worker thread is MTA by default.
    /// If we activated on the STA, the resulting RCW would be apartment-bound
    /// to it and the MTA capture thread's first COM call would fail with
    /// <c>InvalidCastException</c> at <c>GetCOMIPFromRCW</c> — the standard
    /// proxies registered for <c>IAudioCaptureClient</c> can't marshal across
    /// for the proxy returned by <c>ActivateAudioInterfaceAsync</c>.
    /// </summary>
    private void Activate() => RunOnMta(ActivateCore);

    private void ActivateCore()
    {
        var paramsStruct = new AudioClientActivationParams
        {
            ActivationType      = ActivationTypeProcessLoopback,
            TargetProcessId     = (uint)_processId,
            ProcessLoopbackMode = ProcessLoopbackModeIncludeTree,
        };

        var paramsHandle = GCHandle.Alloc(paramsStruct, GCHandleType.Pinned);
        try
        {
            var prop = new PropVariantBlob
            {
                Vt          = PropVariantTypeBlob,
                CbSize      = (uint)Marshal.SizeOf<AudioClientActivationParams>(),
                PBlobData   = paramsHandle.AddrOfPinnedObject(),
            };

            var iidAudioClient = typeof(IAudioClient).GUID;
            var handler        = new ActivationHandler();

            int hr;
            IActivateAudioInterfaceAsyncOperation? operation;
            try
            {
                hr = NativeMethods.ActivateAudioInterfaceAsync(
                    VirtualAudioDeviceProcessLoopback,
                    ref iidAudioClient,
                    ref prop,
                    handler,
                    out operation);
            }
            catch (DllNotFoundException ex)
            {
                throw new InvalidOperationException(
                    "Mmdevapi.dll is not available — process loopback requires Windows 10 build 20348+ / Windows 11.", ex);
            }

            if (hr < 0 || operation is null)
                throw new InvalidOperationException(
                    $"ActivateAudioInterfaceAsync failed for PID {_processId}. " +
                    $"HRESULT 0x{hr:X8}. This API requires Windows 10 build 20348+ / Windows 11.");

            if (!handler.WaitOne(TimeSpan.FromSeconds(5)))
                throw new InvalidOperationException(
                    $"ActivateAudioInterfaceAsync timed out for PID {_processId}.");

            operation.GetActivateResult(out var activateHr, out var unknownObject);
            if (activateHr < 0 || unknownObject is null)
                throw new InvalidOperationException(
                    $"GetActivateResult failed for PID {_processId}. HRESULT 0x{activateHr:X8}.");

            _audioClient = (IAudioClient)unknownObject;

            var formatBytes = WaveFormatToBytes(_format);
            var formatHandle = GCHandle.Alloc(formatBytes, GCHandleType.Pinned);
            try
            {
                // Process loopback Initialize is non-standard: Microsoft's
                // ApplicationLoopback C++ sample passes AUTOCONVERTPCM and
                // SRC_DEFAULT_QUALITY as the *periodicity* slot, not as
                // stream flags. Putting them in the normal streamFlags slot
                // gets us E_INVALIDARG. This is undocumented but is the only
                // shape the OS accepts.
                hr = _audioClient.Initialize(
                    AudioClientShareModeShared,
                    AudioClientStreamFlagsLoopback | AudioClientStreamFlagsEventCallback,
                    BufferDurationHns,
                    AudioClientStreamFlagsAutoConvertPcm | AudioClientStreamFlagsSrcDefaultQuality,
                    formatHandle.AddrOfPinnedObject(),
                    Guid.Empty);

                if (hr < 0)
                    throw new InvalidOperationException(
                        $"IAudioClient::Initialize failed for PID {_processId}. HRESULT 0x{hr:X8}.");
            }
            finally
            {
                formatHandle.Free();
            }

            _eventHandle = NativeMethods.CreateEventW(IntPtr.Zero, false, false, null);
            if (_eventHandle == IntPtr.Zero)
                throw new InvalidOperationException("Could not create event handle for process loopback capture.");

            _audioClient.SetEventHandle(_eventHandle);

            var captureClientGuid = typeof(IAudioCaptureClient).GUID;
            _audioClient.GetService(ref captureClientGuid, out var captureService);
            _captureClient = (IAudioCaptureClient)captureService;
        }
        finally
        {
            paramsHandle.Free();
        }
    }

    public void Start()
    {
        if (_running) return;
        if (_audioClient is null) throw new InvalidOperationException("Capture not activated.");

        RunOnMta(() => _audioClient!.Start());
        _running = true;

        _captureThread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Name         = $"ProcLoopback({ProcessName}:{_processId})",
            Priority     = ThreadPriority.AboveNormal,
        };
        _captureThread.Start();
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;

        RunOnMta(() => { try { _audioClient?.Stop(); } catch { /* process may have died */ } });

        // Wake the capture thread out of its WaitForSingleObject.
        if (_eventHandle != IntPtr.Zero) NativeMethods.SetEvent(_eventHandle);
        _captureThread?.Join(TimeSpan.FromSeconds(2));
        _captureThread = null;
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
            NativeMethods.CloseHandle(_eventHandle);
            _eventHandle = IntPtr.Zero;
        }

        _processHandle?.Dispose();
    }

    /// <summary>
    /// Execute <paramref name="action"/> on an MTA thread. If the current
    /// thread is already MTA we run inline. Otherwise we spin a one-shot
    /// MTA thread, run the work there, and join. Used to ensure the WASAPI
    /// COM objects are bound to the MTA so the capture worker (also MTA)
    /// can call them without apartment marshaling.
    /// </summary>
    private static void RunOnMta(Action action)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA)
        {
            action();
            return;
        }

        Exception? error = null;
        var t = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        })
        {
            IsBackground = true,
            Name         = "ProcLoopback(MTA bridge)",
        };
        t.SetApartmentState(ApartmentState.MTA);
        t.Start();
        t.Join();
        if (error is not null) throw error;
    }

    private void CaptureLoop()
    {
        // MMCSS belongs on the thread that talks to the device.
        var mmcss = Mmcss.Begin();
        try
        {
            CaptureLoopCore();
        }
        finally
        {
            Mmcss.Revert(mmcss);
        }
    }

    private void CaptureLoopCore()
    {
        const uint waitMs = 100;
        var bytesPerFrame = _format.BlockAlign;
        var maxFrames     = (uint)(_scratchBytes.Length / bytesPerFrame);

        while (_running)
        {
            var rc = NativeMethods.WaitForSingleObject(_eventHandle, waitMs);
            if (!_running) break;
            // 0 = signalled (data ready), 0x102 = timeout (silent app — keep polling).
            if (rc != 0 && rc != 0x102)
            {
                _faulted = true;
                break;
            }

            while (true)
            {
                if (_captureClient is null) return;

                var hr = _captureClient.GetNextPacketSize(out var packetFrames);
                if (hr < 0) { _faulted = true; return; }
                if (packetFrames == 0) break;

                hr = _captureClient.GetBuffer(out var dataPtr, out var framesAvailable, out var flags, out _, out _);
                if (hr < 0) { _faulted = true; return; }

                try
                {
                    var frames = (int)Math.Min(framesAvailable, maxFrames);
                    if (frames > 0)
                    {
                        if ((flags & BufferFlagsSilent) != 0)
                        {
                            var silence = _scratch.AsSpan(0, frames * 2);
                            silence.Clear();
                            _ring.Write(silence);
                        }
                        else
                        {
                            var byteCount = frames * bytesPerFrame;
                            Marshal.Copy(dataPtr, _scratchBytes, 0, byteCount);
                            var written = WaveFormatX.ConvertToStereo(
                                _scratchBytes.AsSpan(0, byteCount), _format, _scratch);
                            _ring.Write(_scratch.AsSpan(0, written * 2));
                        }
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

    /// <summary>
    /// Build the raw 18-byte <c>WAVEFORMATEX</c> blob the native
    /// <c>IAudioClient::Initialize</c> wants. We must NOT use NAudio's
    /// <c>WaveFormat.Serialize</c> here — that helper prepends a 4-byte
    /// length field for RIFF-chunk framing, which the OS happily
    /// misinterprets as the leading <c>wFormatTag</c>/<c>nChannels</c>
    /// fields and bails with E_INVALIDARG before reading anything else.
    /// </summary>
    private static byte[] WaveFormatToBytes(WaveFormat fmt)
    {
        var bytes = new byte[18];
        var span  = bytes.AsSpan();
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(span.Slice( 0, 2), (short)fmt.Encoding);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(span.Slice( 2, 2), (short)fmt.Channels);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span.Slice( 4, 4),         fmt.SampleRate);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span.Slice( 8, 4),         fmt.AverageBytesPerSecond);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(span.Slice(12, 2), (short)fmt.BlockAlign);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(span.Slice(14, 2), (short)fmt.BitsPerSample);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(span.Slice(16, 2), 0); // cbSize — no extra data after WAVEFORMATEX.
        return bytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientActivationParams
    {
        public uint ActivationType;       // 0 = Default, 1 = ProcessLoopback
        public uint TargetProcessId;
        public uint ProcessLoopbackMode;  // 0 = Include tree, 1 = Exclude tree
    }

    /// <summary>
    /// PROPVARIANT laid out for the BLOB variant only. Field offsets match the
    /// 64-bit Windows union: 8-byte header, then a ULONG cbSize (offset 8),
    /// 4 bytes of padding for natural alignment, then the LPVOID pBlobData
    /// at offset 16. Total 24 bytes — what
    /// <see cref="NativeMethods.ActivateAudioInterfaceAsync"/> reads.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariantBlob
    {
        public ushort Vt;
        public ushort WReserved1;
        public ushort WReserved2;
        public ushort WReserved3;
        public uint   CbSize;
        public uint   Padding;
        public IntPtr PBlobData;
    }

    [Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig]
        int Initialize(
            uint shareMode,
            uint streamFlags,
            long bufferDuration,
            long periodicity,
            IntPtr format,
            Guid audioSessionGuid);

        [PreserveSig] int GetBufferSize(out uint bufferFrameCount);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(uint shareMode, IntPtr format, IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);

        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);

        [PreserveSig]
        int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig]
        int GetBuffer(
            out IntPtr buffer,
            out uint   numFramesToRead,
            out uint   flags,
            out ulong  devicePosition,
            out ulong  qpcPosition);

        [PreserveSig] int ReleaseBuffer(uint numFramesRead);
        [PreserveSig] int GetNextPacketSize(out uint numFramesInNextPacket);
    }

    [Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig]
        int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object? activatedInterface);
    }

    [Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        [PreserveSig]
        int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
    }

    /// <summary>
    /// Minimal completion handler: signals a wait handle when the OS finishes
    /// activating. The handler instance is held alive by the GC root we keep
    /// in the activation call frame (local var) — the COM ref-count is via the
    /// CCW automatically generated by the runtime.
    /// </summary>
    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler
    {
        private readonly ManualResetEventSlim _done = new(false);

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            _done.Set();
            return 0; // S_OK
        }

        public bool WaitOne(TimeSpan timeout) => _done.Wait(timeout);
    }

    private static class NativeMethods
    {
        [DllImport("Mmdevapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        public static extern int ActivateAudioInterfaceAsync(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
            ref Guid riid,
            ref PropVariantBlob activationParams,
            IActivateAudioInterfaceCompletionHandler completionHandler,
            out IActivateAudioInterfaceAsyncOperation activationOperation);

        [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateEventW(IntPtr lpEventAttributes, bool manualReset, bool initialState, string? name);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetEvent(IntPtr handle);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        public static extern SafeProcessHandle OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);
    }
}
