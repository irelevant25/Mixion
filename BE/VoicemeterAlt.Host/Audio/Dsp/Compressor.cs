using VoicemeterAlt.Host.State;

namespace VoicemeterAlt.Host.Audio.Dsp;

/// <summary>
/// Feed-forward peak compressor with separate attack and release time
/// constants, soft knee around the threshold, and a make-up gain stage.
///
/// The detector runs in dB on the per-sample peak (|x|): a one-pole
/// follower whose coefficient picks attack vs release based on the
/// direction of the level change. The static curve is the standard
/// piecewise function with a quadratic soft knee:
///
///   - below knee (env &lt; threshold − knee/2):     no reduction
///   - inside knee (threshold ± knee/2):              quadratic transition
///   - above knee (env &gt; threshold + knee/2):     overshoot · (1 − 1/ratio)
///
/// Make-up gain is applied as a smoothed linear factor (BE-078) so toggling
/// or sliding it doesn't click. The maximum dB of gain reduction observed
/// across the last block is exposed via <see cref="GainReductionDb"/> for
/// telemetry / the FE compressor meter.
///
/// No allocations on the hot path. <see cref="Process"/> walks the span
/// once per call.
/// </summary>
public sealed class Compressor
{
    /// <summary>Length of the make-up-gain crossfade in samples (~10 ms @ 48 kHz).</summary>
    public const int MakeupFadeSamples = 480;

    private readonly int _sampleRate;

    private bool  _enabled;
    private float _thresholdDb;
    private float _ratio = 1f;
    private float _attackCoeff = 1f;
    private float _releaseCoeff = 1f;
    private float _kneeDb;

    private float _envDb = -120f;

    /// <summary>Smoothed make-up linear factor (BE-078).</summary>
    private float _makeupCurrent = 1f;
    private float _makeupTarget  = 1f;

    /// <summary>
    /// Last applied <see cref="CompressorState"/> shape — used so an
    /// unchanged state swap doesn't reset the smoother.
    /// </summary>
    private CompressorState? _lastApplied;

    public int  SampleRate      => _sampleRate;
    public bool Enabled         => _enabled;
    public float GainReductionDb { get; private set; }

    public Compressor(int sampleRate)
    {
        _sampleRate = sampleRate;
    }

    public void Apply(CompressorState? state)
    {
        if (ReferenceEquals(state, _lastApplied)) return;
        _lastApplied = state;

        if (state is null || !state.Enabled)
        {
            _enabled = false;
            // Aim makeup at unity so when the stage re-enables it doesn't
            // jump to the previous boost.
            _makeupTarget = 1f;
            GainReductionDb = 0f;
            return;
        }

        _enabled       = true;
        _thresholdDb   = state.ThresholdDb;
        _ratio         = MathF.Max(1f, state.Ratio);
        _kneeDb        = MathF.Max(0f, state.KneeDb);
        _attackCoeff   = TimeConstantCoefficient(state.AttackMs);
        _releaseCoeff  = TimeConstantCoefficient(state.ReleaseMs);
        _makeupTarget  = MathF.Pow(10f, state.MakeupDb / 20f);
    }

    public void Process(Span<float> samples)
    {
        if (!_enabled)
        {
            // Even when bypassed, ease makeup back to unity so a re-enable
            // is glitch-free.
            EaseMakeup(samples.Length);
            return;
        }

        var maxGr = 0f;
        var envDb = _envDb;
        var makeupCurrent = _makeupCurrent;
        var makeupTarget  = _makeupTarget;
        var makeupStep    = (makeupTarget - makeupCurrent) / MakeupFadeSamples;

        for (var i = 0; i < samples.Length; i++)
        {
            var x = samples[i];

            // dB-domain peak detector. Dead-floor at -120 dB to avoid
            // log10(0) on silence and to keep the envelope from trailing
            // off into NaN territory.
            var absX     = MathF.Abs(x);
            var inputDb  = absX > 1e-9f ? 20f * MathF.Log10(absX) : -120f;
            var coeff    = inputDb > envDb ? _attackCoeff : _releaseCoeff;
            envDb       += (inputDb - envDb) * coeff;

            // Static curve with quadratic soft knee.
            var overshoot = envDb - _thresholdDb;
            var halfKnee  = _kneeDb * 0.5f;
            float reductionDb;
            if (_kneeDb > 0f && overshoot > -halfKnee && overshoot < halfKnee)
            {
                var t = overshoot + halfKnee;       // 0..knee
                reductionDb = -((1f - 1f / _ratio) * (t * t) / (2f * _kneeDb));
            }
            else if (overshoot >= halfKnee)
            {
                reductionDb = -(overshoot - overshoot / _ratio);
            }
            else
            {
                reductionDb = 0f;
            }

            var grAbs = -reductionDb;
            if (grAbs > maxGr) maxGr = grAbs;

            // dB → linear; multiply by smoothed make-up.
            var gainLin = MathF.Pow(10f, reductionDb / 20f) * makeupCurrent;
            samples[i]  = x * gainLin;

            if (makeupCurrent != makeupTarget)
            {
                makeupCurrent += makeupStep;
                if ((makeupStep > 0f && makeupCurrent > makeupTarget) ||
                    (makeupStep < 0f && makeupCurrent < makeupTarget))
                {
                    makeupCurrent = makeupTarget;
                }
            }
        }

        _envDb          = envDb;
        _makeupCurrent  = makeupCurrent;
        GainReductionDb = maxGr;
    }

    private void EaseMakeup(int frames)
    {
        if (_makeupCurrent == _makeupTarget) return;
        var step = (_makeupTarget - _makeupCurrent) / MakeupFadeSamples;
        var advance = step * frames;
        _makeupCurrent += advance;
        if ((step > 0f && _makeupCurrent > _makeupTarget) ||
            (step < 0f && _makeupCurrent < _makeupTarget))
        {
            _makeupCurrent = _makeupTarget;
        }
    }

    /// <summary>
    /// Convert a millisecond time constant into the per-sample one-pole
    /// coefficient <c>1 − e^(−1/τ)</c>. <paramref name="ms"/> ≤ 0 means
    /// "instant" (coeff = 1).
    /// </summary>
    private float TimeConstantCoefficient(float ms)
    {
        if (ms <= 0f) return 1f;
        var tauSamples = ms * 0.001f * _sampleRate;
        return 1f - MathF.Exp(-1f / tauSamples);
    }
}
