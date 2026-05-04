using System.Runtime.InteropServices;
using NAudio.Wave;

namespace Mixion.Host.Audio;

/// <summary>
/// Native WASAPI plumbing used by <see cref="LowLatencyCaptureDevice"/> and
/// <see cref="LowLatencyRenderDevice"/>. We can't go through NAudio's
/// <c>WasapiCapture</c> / <c>WasapiOut</c> because they call
/// <c>IAudioClient::Initialize</c> internally — and the only way to get
/// the OS audio engine onto the 3 ms low-latency period is to call
/// <c>IAudioClient3::InitializeSharedAudioStream</c> instead of plain
/// <c>Initialize</c>. So the two "LowLatency*" device classes drive WASAPI
/// directly via these COM interfaces.
///
/// Apartment rules: every COM call into these interfaces must run on an
/// MTA thread. The host entry point is <c>[STAThread]</c> for the WinForms
/// tray pump, so any direct activation from the entry thread would bind
/// the resulting RCW to the STA — and our capture/render workers (MTA by
/// default) would then fail with apartment-mismatch errors. Both device
/// classes route activation through an MTA bridge thread; see
/// <c>RunOnMta</c> in each.
/// </summary>
internal static class LowLatencyComInterop
{
    public const uint AUDCLNT_SHAREMODE_SHARED = 0;

    public const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK     = 0x00040000;
    public const uint AUDCLNT_STREAMFLAGS_NOPERSIST         = 0x00080000;
    public const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM    = 0x80000000;
    public const uint AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;

    /// <summary>Render endpoint (DataFlow.Render).</summary>
    public const uint EDATAFLOW_RENDER  = 0;
    /// <summary>Capture endpoint (DataFlow.Capture).</summary>
    public const uint EDATAFLOW_CAPTURE = 1;

    /// <summary>Default endpoint role used for activation lookups.</summary>
    public const uint EROLE_CONSOLE = 0;

    /// <summary>CLSID_MMDeviceEnumerator — used with CoCreateInstance.</summary>
    public static readonly Guid CLSID_MMDeviceEnumerator =
        new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    public static readonly Guid IID_IMMDeviceEnumerator =
        new("A95664D2-9614-4F35-A746-DE8DB63617E6");

    public static readonly Guid IID_IAudioClient3 =
        new("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42");

    public static readonly Guid IID_IAudioCaptureClient =
        new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

    public static readonly Guid IID_IAudioRenderClient =
        new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");

    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern int CoCreateInstance(
        ref Guid clsid,
        IntPtr   outer,
        uint     clsContext,
        ref Guid iid,
        [MarshalAs(UnmanagedType.IUnknown)] out object instance);

    public const uint CLSCTX_INPROC_SERVER = 0x1;

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateEventW(IntPtr lpEventAttributes, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetEvent(IntPtr handle);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr handle, uint timeoutMs);

    /// <summary>Run an action on a dedicated MTA thread and join. See class doc for the apartment rationale.</summary>
    public static void RunOnMta(Action action)
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
            Name         = "LowLatency(MTA bridge)",
        };
        t.SetApartmentState(ApartmentState.MTA);
        t.Start();
        t.Join();
        if (error is not null) throw error;
    }

    /// <summary>
    /// Build the raw 18-byte WAVEFORMATEX layout the OS expects from the
    /// managed <see cref="WaveFormat"/>. NAudio's <c>WaveFormat.Serialize</c>
    /// emits a 4-byte length prefix for RIFF chunk framing — wrong shape
    /// for <c>IAudioClient::Initialize</c>, which will return E_INVALIDARG
    /// if you hand it the prefix.
    /// </summary>
    public static byte[] WaveFormatToBytes(WaveFormat fmt)
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

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(uint dataFlow, uint stateMask, [MarshalAs(UnmanagedType.IUnknown)] out object devices);
        [PreserveSig] int GetDefaultAudioEndpoint(uint dataFlow, uint role, [MarshalAs(UnmanagedType.Interface)] out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Interface)] out IMMDevice endpoint);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDevice
    {
        [PreserveSig]
        int Activate(
            ref Guid iid,
            uint     clsCtx,
            IntPtr   activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);

        [PreserveSig] int OpenPropertyStore(uint stgmAccess, IntPtr propertyStore);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [Guid("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioClient3
    {
        // IAudioClient methods
        [PreserveSig]
        int Initialize(
            uint   shareMode,
            uint   streamFlags,
            long   bufferDuration,
            long   periodicity,
            IntPtr format,
            IntPtr audioSessionGuid);

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
        [PreserveSig] int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object service);

        // IAudioClient2 methods
        [PreserveSig] int IsOffloadCapable(uint category, out bool offloadCapable);
        [PreserveSig] int SetClientProperties(IntPtr properties);
        [PreserveSig] int GetBufferSizeLimits(IntPtr format, bool eventDriven, out long minBufferDuration, out long maxBufferDuration);

        // IAudioClient3 methods
        [PreserveSig]
        int GetSharedModeEnginePeriod(
            IntPtr   format,
            out uint defaultPeriodInFrames,
            out uint fundamentalPeriodInFrames,
            out uint minPeriodInFrames,
            out uint maxPeriodInFrames);

        [PreserveSig]
        int GetCurrentSharedModeEnginePeriod(out IntPtr format, out uint currentPeriodInFrames);

        [PreserveSig]
        int InitializeSharedAudioStream(
            uint   streamFlags,
            uint   periodInFrames,
            IntPtr format,
            IntPtr audioSessionGuid);
    }

    [Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioCaptureClient
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

    [Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(uint numFramesRequested, out IntPtr buffer);
        [PreserveSig] int ReleaseBuffer(uint numFramesWritten, uint flags);
    }
}
