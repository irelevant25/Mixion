using Mixion.Host.State;

namespace Mixion.Host.Audio.Dsp;

/// <summary>
/// Down-ward expander acting as a noise gate. Has hysteresis between an
/// open and a close threshold (close ≈ open − 6 dB) so input that hovers
/// around the threshold doesn't chatter the gate state. Once closed, the
/// gate attenuates by <see cref="GateState.RangeDb"/> (typically negative,
/// e.g. −40 dB) rather than going hard silent — this preserves a touch of
/// room tone and matches mainstream gate plugins.
///
/// Time constants:
///   - <c>AttackMs</c>:   how fast the envelope moves toward "open".
///   - <c>HoldMs</c>:     how long the gate stays open after the envelope
///                          drops below the open threshold (only after that
///                          do we start closing).
///   - <c>ReleaseMs</c>:  how fast the envelope moves toward "closed" once
///                          hold has elapsed.
///
/// No allocations on the audio path.
/// </summary>
public sealed class NoiseGate
{
    /// <summary>How much hysteresis below the open threshold to count as "closed enough".</summary>
    public const float HysteresisDb = 6f;

    private readonly int _sampleRate;

    private bool  _enabled;
    private float _openDb;
    private float _closeDb;
    private float _attackCoeff = 1f;
    private float _releaseCoeff = 1f;
    private int   _holdSamples;
    private float _rangeLin = 1f;

    private float _envDb = -120f;
    private float _currentGain = 1f;
    private bool  _open;
    private int   _holdCounter;

    private GateState? _lastApplied;

    public bool Enabled    => _enabled;
    public int  SampleRate => _sampleRate;

    public NoiseGate(int sampleRate)
    {
        _sampleRate = sampleRate;
    }

    public void Apply(GateState? state)
    {
        if (ReferenceEquals(state, _lastApplied)) return;
        _lastApplied = state;

        if (state is null || !state.Enabled)
        {
            _enabled = false;
            return;
        }

        _enabled      = true;
        _openDb       = state.ThresholdDb;
        _closeDb      = state.ThresholdDb - HysteresisDb;
        _attackCoeff  = TimeConstantCoefficient(state.AttackMs);
        _releaseCoeff = TimeConstantCoefficient(state.ReleaseMs);
        _holdSamples  = (int)MathF.Max(0f, state.HoldMs * 0.001f * _sampleRate);
        // RangeDb is the floor in dB; -40 dB → ~0.01 linear. A non-negative
        // value would be meaningless so clamp ≤ 0.
        var rangeDb   = MathF.Min(0f, state.RangeDb);
        _rangeLin     = MathF.Pow(10f, rangeDb / 20f);
    }

    public void Process(Span<float> samples)
    {
        if (!_enabled)
        {
            // Make sure the gate fully opens when bypassed so a re-enable
            // doesn't snap from a stale "closed" position.
            _currentGain = 1f;
            _open = true;
            _holdCounter = 0;
            return;
        }

        var envDb       = _envDb;
        var currentGain = _currentGain;
        var open        = _open;
        var hold        = _holdCounter;

        for (var i = 0; i < samples.Length; i++)
        {
            var x = samples[i];

            var absX = MathF.Abs(x);
            // Track the input level in dB with an instantaneous peak — the
            // gate's own attack/release smooths the *gain*, not the
            // detector, which keeps it responsive on sharp transients.
            var inputDb = absX > 1e-9f ? 20f * MathF.Log10(absX) : -120f;
            envDb = inputDb; // peak detector

            if (envDb >= _openDb)
            {
                open = true;
                hold = _holdSamples;
            }
            else if (envDb < _closeDb)
            {
                if (open)
                {
                    if (hold > 0) hold--;
                    if (hold == 0) open = false;
                }
            }

            var targetGain = open ? 1f : _rangeLin;
            var coeff      = targetGain > currentGain ? _attackCoeff : _releaseCoeff;
            currentGain   += (targetGain - currentGain) * coeff;

            samples[i] = x * currentGain;
        }

        _envDb        = envDb;
        _currentGain  = currentGain;
        _open         = open;
        _holdCounter  = hold;
    }

    private float TimeConstantCoefficient(float ms)
    {
        if (ms <= 0f) return 1f;
        var tauSamples = ms * 0.001f * _sampleRate;
        return 1f - MathF.Exp(-1f / tauSamples);
    }
}
