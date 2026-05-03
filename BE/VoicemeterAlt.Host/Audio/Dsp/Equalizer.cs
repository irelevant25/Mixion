using VoicemeterAlt.Host.State;

namespace VoicemeterAlt.Host.Audio.Dsp;

/// <summary>
/// Cascade of biquads driven by an <see cref="EqState"/>. To avoid zipper
/// noise on band moves (BE-072 / BE-078), coefficient changes are not
/// applied in place. Instead we keep two parallel cascades:
///
///   • <c>_primary</c> — the new state, started from zero delay state.
///   • <c>_shadow</c>  — the previous state, kept running for the duration
///                        of a short crossfade so the audible signal is the
///                        sum <c>(1 − t)·primary + t·shadow</c>.
///
/// During the crossfade window (~10 ms), every Apply that changes
/// coefficients moves the live cascade into shadow and reseeds primary.
/// After the window elapses the shadow output is discarded. This is more
/// expensive per sample for a few hundred samples after every band tweak,
/// but the audio is glitch-free under continuous slider drags.
///
/// Allocations: zero on the audio path. The two cascades are pre-allocated
/// at construction up to <see cref="MaxBands"/>; bands beyond that are
/// silently ignored (the FE caps the band count well below this).
/// </summary>
public sealed class Equalizer
{
    /// <summary>
    /// Crossfade length in samples. Picked so a band drag at 30 Hz finishes
    /// each fade comfortably before the next one starts (33 ms &gt; 10 ms).
    /// </summary>
    public const int CrossfadeSamples = 480; // ~10 ms @ 48 kHz

    /// <summary>Hard cap on bands to keep buffers bounded; the FE never sends this many.</summary>
    public const int MaxBands = 16;

    private readonly int _sampleRate;

    private readonly BiquadFilter[] _primary;
    private readonly BiquadFilter[] _shadow;
    private int _primaryCount;
    private int _shadowCount;

    /// <summary>
    /// Snapshot of the band parameters currently driving <c>_primary</c>.
    /// Used in <see cref="Apply"/> to detect "nothing changed" and skip the
    /// crossfade churn entirely.
    /// </summary>
    private readonly EqBand[] _primaryBands;
    private bool _primaryEnabled;

    private int _fadeRemaining;

    public bool   Enabled       => _primaryEnabled && _primaryCount > 0;
    public int    BandCount     => _primaryCount;
    public int    SampleRate    => _sampleRate;

    public Equalizer(int sampleRate)
    {
        _sampleRate   = sampleRate;
        _primary      = new BiquadFilter[MaxBands];
        _shadow       = new BiquadFilter[MaxBands];
        _primaryBands = new EqBand[MaxBands];
        for (var i = 0; i < MaxBands; i++)
        {
            _primary[i] = new BiquadFilter();
            _shadow[i]  = new BiquadFilter();
        }
    }

    /// <summary>
    /// Reconfigure the cascade from a new <see cref="EqState"/>. <c>null</c>
    /// or <c>state.Enabled == false</c> bypasses the EQ. Called on the audio
    /// thread once per tick when the state reference changed; pre-existing
    /// band parameters are reused as-is so a no-op state swap costs only a
    /// few comparisons.
    /// </summary>
    public void Apply(EqState? state)
    {
        var newEnabled = state?.Enabled == true;
        var newBands   = newEnabled ? state!.Bands : Array.Empty<EqBand>();
        var newCount   = Math.Min(newBands.Length, MaxBands);

        if (!HasChanged(newEnabled, newBands, newCount)) return;

        // Keep currently-running primary as the "shadow" so its tail
        // continues to play out under the crossfade. We move references
        // around (filters are reusable) — both arrays are pre-allocated.
        if (_primaryCount > 0)
        {
            for (var i = 0; i < _primaryCount; i++)
            {
                (_shadow[i], _primary[i]) = (_primary[i], _shadow[i]);
            }
            _shadowCount = _primaryCount;
        }
        else
        {
            _shadowCount = 0;
        }

        _primaryCount   = newCount;
        _primaryEnabled = newEnabled;

        for (var i = 0; i < newCount; i++)
        {
            var b = newBands[i];
            _primary[i].SetBand(b.Type, b.Frequency, b.GainDb, b.Q, _sampleRate);
            // Fresh delay line for the new coefficients keeps the
            // pre-fade-in primary output stable from sample 0.
            _primary[i].Reset();
            _primaryBands[i] = b;
        }

        // Only run a crossfade when there is actually something old to
        // fade out. First-ever Apply just snaps in and produces silence
        // on shadow.
        _fadeRemaining = _shadowCount > 0 ? CrossfadeSamples : 0;
    }

    /// <summary>
    /// Process a block in place: <c>(1 − t)·primary + t·shadow</c> while
    /// the crossfade is in flight, primary alone afterwards. Bypassed when
    /// the EQ is disabled.
    /// </summary>
    public void Process(Span<float> samples)
    {
        if (_primaryCount == 0 && _shadowCount == 0) return;

        if (_fadeRemaining > 0 && _shadowCount > 0)
        {
            var fadeStep = 1f / CrossfadeSamples;
            for (var s = 0; s < samples.Length; s++)
            {
                var x = samples[s];

                var primaryOut = x;
                for (var b = 0; b < _primaryCount; b++)
                    primaryOut = _primary[b].Process(primaryOut);

                var shadowOut = x;
                for (var b = 0; b < _shadowCount; b++)
                    shadowOut = _shadow[b].Process(shadowOut);

                if (_fadeRemaining > 0)
                {
                    var t = (float)_fadeRemaining * fadeStep; // 1.0 → 0.0
                    samples[s] = primaryOut * (1f - t) + shadowOut * t;
                    _fadeRemaining--;
                }
                else
                {
                    samples[s] = primaryOut;
                }
            }

            if (_fadeRemaining == 0) _shadowCount = 0;
        }
        else
        {
            // No fade in flight — straight cascade.
            for (var b = 0; b < _primaryCount; b++)
                _primary[b].Process(samples);
        }
    }

    /// <summary>
    /// True if the requested state differs from what's currently driving
    /// the primary cascade. Comparison is by value (band id + parameters)
    /// so a structurally-identical state coming over the wire doesn't
    /// trigger a needless crossfade.
    /// </summary>
    private bool HasChanged(bool newEnabled, EqBand[] newBands, int newCount)
    {
        if (newEnabled != _primaryEnabled) return true;
        if (newCount   != _primaryCount)   return true;

        for (var i = 0; i < newCount; i++)
        {
            var a = _primaryBands[i];
            var b = newBands[i];
            if (a is null) return true;
            if (a.Type      != b.Type)      return true;
            if (a.Frequency != b.Frequency) return true;
            if (a.GainDb    != b.GainDb)    return true;
            if (a.Q         != b.Q)         return true;
            if (a.Id        != b.Id)        return true;
        }
        return false;
    }
}
