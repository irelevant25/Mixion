using NAudio.CoreAudioApi;
using NAudio.Wave;
using Mixion.Host.Interop;

namespace Mixion.Host.Audio;

/// <summary>
/// Render endpoint opened in WASAPI <c>Exclusive</c> mode. Bypasses the
/// Windows shared-mode mixer entirely — no other process can play to this
/// device while the engine holds it open — in exchange for periods well
/// below the 10 ms shared-mode floor (typically 3–5 ms on consumer
/// hardware, sometimes lower on pro audio interfaces).
///
/// Trade-offs the user opts into when flipping a device into exclusive:
/// <list type="bullet">
///   <item>System sounds, browser audio, Discord, Spotify etc. silently fail to play through this device until they pick a different one or the engine releases it.</item>
///   <item>Format negotiation is strict — the device's exclusive-mode format may differ from its shared-mode mix format. We probe the mix format first and fall back to a few common shapes.</item>
///   <item>Virtual cables (CABLE Input, Voicemeeter Input) usually reject exclusive mode entirely; <see cref="EngineFactory"/> catches that and falls back to a shared-mode device.</item>
/// </list>
///
/// Same external shape as <see cref="RenderDevice"/>: the mix engine writes
/// interleaved stereo float pairs into <see cref="Ring"/>; NAudio's
/// <see cref="WasapiOut"/> drains it on its own render thread. No COM
/// activation magic here — NAudio's <c>WasapiOut</c> already supports
/// exclusive mode via its constructor.
/// </summary>
public sealed class ExclusiveRenderDevice : IRenderDevice
{
    private readonly MMDevice                _device;
    private readonly WasapiOut               _output;
    private readonly RingBuffer              _ring;
    private readonly RingBufferWaveProvider  _provider;
    private volatile bool                    _faulted;

    public string Id           => _device.ID;
    public string FriendlyName => _device.FriendlyName;
    public int    SampleRate   => _output.OutputWaveFormat.SampleRate;
    public int    DestChannels => _output.OutputWaveFormat.Channels;
    public int    BitsPerSample => _output.OutputWaveFormat.BitsPerSample;
    public RingBuffer Ring     => _ring;
    public int    LatencyMs    { get; }
    // Exclusive-mode buffer is the full requested latency — nothing else is
    // shaping it. Treat that as the "period" for MixEngine block alignment.
    public int    BufferFrames => Math.Max(1, LatencyMs * SampleRate / 1000);
    public RenderMode Mode => RenderMode.Exclusive;
    // Successful exclusive opens never have a fallback reason — kept for
    // interface symmetry. Settable to keep the contract uniform across
    // device classes.
    public string? ExclusiveFallbackReason { get; set; }
    public bool   IsFaulted    => _faulted;

    /// <summary>
    /// HRESULT <c>AUDCLNT_E_DEVICE_IN_USE</c>. NAudio surfaces it as the
    /// hex string in the COM exception message. Most common cause when
    /// flipping a device from shared to exclusive: <em>our own</em>
    /// previous shared-mode handle hasn't fully released yet — Windows
    /// keeps the endpoint in a "transitioning" state for a few tens of
    /// ms after <c>IAudioClient::Stop</c> returns.
    /// </summary>
    private const string DeviceInUseHr = "0x8889000A";

    /// <summary>Backoff between retries on <see cref="DeviceInUseHr"/>. Total budget = retries × delay.</summary>
    private const int DeviceInUseRetryDelayMs = 60;

    /// <summary>
    /// Number of <see cref="DeviceInUseHr"/> retries before giving up and
    /// throwing. 4 × 60 ms = 240 ms is well above the typical Windows
    /// release window (a few tens of ms) and well below human-noticeable
    /// UI lag.
    /// </summary>
    private const int DeviceInUseRetries = 4;

    public ExclusiveRenderDevice(MMDevice device, int ringCapacityFrames, int latencyMs)
    {
        _device = device;

        // NAudio's WasapiOut Init() will throw if the device rejects the
        // wave format the provider exposes. In exclusive mode that's common
        // — the device may insist on a specific PCM bit depth that doesn't
        // match the shared-mode mix format. We try the mix format first
        // (works on most modern interfaces), then fall back to plain PCM at
        // the device's reported rate; if both fail the constructor throws
        // and EngineFactory falls back to a shared-mode device class.
        //
        // For each candidate we also retry on AUDCLNT_E_DEVICE_IN_USE — the
        // transient self-collision after a shared→exclusive rebuild.
        var mixFmt = device.AudioClient.MixFormat;

        WasapiOut?              output    = null;
        RingBuffer?             ring      = null;
        RingBufferWaveProvider? provider  = null;
        Exception?              lastError = null;
        var                     opened    = false;

        foreach (var candidate in CandidateFormats(mixFmt))
        {
            for (var attempt = 0; attempt <= DeviceInUseRetries; attempt++)
            {
                try
                {
                    ring     = new RingBuffer(ringCapacityFrames * 2);
                    provider = new RingBufferWaveProvider(ring, candidate);
                    output   = new WasapiOut(
                        device,
                        AudioClientShareMode.Exclusive,
                        useEventSync: true,
                        latency:      latencyMs);
                    output.Init(provider);
                    opened = true;
                    break;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    // Cleanup any partially-built output before retrying so
                    // we don't leak a half-initialised WasapiOut instance.
                    output?.Dispose();
                    output   = null;
                    provider = null;
                    ring     = null;

                    // Self-collision after a rebuild — sleep briefly and
                    // retry the same candidate. Anything else (format
                    // mismatch, alignment, period) is a hard fail; move on.
                    if (IsDeviceInUse(ex) && attempt < DeviceInUseRetries)
                    {
                        Thread.Sleep(DeviceInUseRetryDelayMs);
                        continue;
                    }
                    break;
                }
            }
            if (opened) break;
        }

        if (output is null || provider is null || ring is null)
            throw new NotSupportedException(
                $"Could not open '{device.FriendlyName}' in WASAPI exclusive mode at {latencyMs} ms. " +
                $"Last error: {lastError?.Message ?? "format negotiation failed"}.",
                lastError);

        _ring      = ring;
        _output    = output;
        _provider  = provider;
        LatencyMs  = latencyMs;

        _output.PlaybackStopped += OnPlaybackStopped;
    }

    /// <summary>A stream that died under us stops with an exception; a plain <see cref="Stop"/> doesn't.</summary>
    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null) _faulted = true;
    }

    /// <summary>
    /// Walk the inner-exception chain looking for the
    /// <see cref="DeviceInUseHr"/> string in any message. NAudio nests COM
    /// HRESULTs a few levels deep and the deepest one is where the actual
    /// hex code lives.
    /// </summary>
    private static bool IsDeviceInUse(Exception ex)
    {
        var cur = ex;
        while (cur is not null)
        {
            if (cur.Message is { } m && m.Contains(DeviceInUseHr, StringComparison.OrdinalIgnoreCase))
                return true;
            cur = cur.InnerException;
        }
        return false;
    }

    /// <summary>
    /// Format candidates to try, in order of preference. The mix format
    /// (32-bit float) is the lingua franca on Windows 10+ consumer
    /// hardware; if a device rejects it we have nowhere to go that
    /// <see cref="RingBufferWaveProvider"/> can produce, so the loop is a
    /// single attempt for now. Kept as a method so we can grow the list
    /// later (e.g. once the provider learns to emit PCM 16/24).
    /// </summary>
    private static IEnumerable<WaveFormat> CandidateFormats(WaveFormat mixFormat)
    {
        if (WaveFormatX.IsFloat(mixFormat) && mixFormat.BitsPerSample == 32)
            yield return mixFormat;
        // Future: add PCM 16/24 candidates once RingBufferWaveProvider
        // can produce them. For now exclusive mode requires the device's
        // exclusive format to accept 32-bit float.
    }

    // MMCSS is registered on NAudio's playback thread by RingBufferWaveProvider.
    public void Start() => _output.Play();

    public void Stop()
    {
        try { _output.Stop(); } catch { /* device may have vanished */ }
    }

    public void Dispose()
    {
        Stop();
        _output.PlaybackStopped -= OnPlaybackStopped;
        _output.Dispose();
        _device.Dispose();
    }
}
