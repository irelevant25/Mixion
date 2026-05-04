using System.Runtime.CompilerServices;
using Mixion.Host.State;

namespace Mixion.Host.Audio.Dsp;

/// <summary>
/// Single biquad section with normalised coefficients (RBJ cookbook). The
/// transfer function is
/// <code>H(z) = (b0 + b1·z⁻¹ + b2·z⁻²) / (1 + a1·z⁻¹ + a2·z⁻²)</code>
/// — i.e. a0 has already been divided through.
///
/// Sample-by-sample state lives in <see cref="_z1"/> / <see cref="_z2"/>
/// (Transposed Direct Form II — fewer multiplies on the audio path and good
/// numerical behaviour for the parameter ranges this app uses). Both
/// <see cref="Process(float)"/> and the block <see cref="Process(Span{float})"/>
/// allocate nothing.
/// </summary>
public sealed class BiquadFilter
{
    private float _b0 = 1f, _b1, _b2;
    private float _a1, _a2;
    private float _z1, _z2;

    public float B0 => _b0;
    public float B1 => _b1;
    public float B2 => _b2;
    public float A1 => _a1;
    public float A2 => _a2;

    /// <summary>
    /// Set the five normalised coefficients directly. Used by callers that
    /// already computed them (e.g. <see cref="Equalizer"/> reusing one
    /// formula across both the audio biquad and a UI mirror).
    /// </summary>
    public void SetCoefficients(float b0, float b1, float b2, float a1, float a2)
    {
        _b0 = b0;
        _b1 = b1;
        _b2 = b2;
        _a1 = a1;
        _a2 = a2;
    }

    /// <summary>
    /// Compute + apply RBJ-cookbook coefficients for the given band shape and
    /// sample rate. Convenience over <see cref="ComputeCoefficients"/>.
    /// </summary>
    public void SetBand(EqBandType type, float frequency, float gainDb, float q, int sampleRate)
    {
        ComputeCoefficients(type, frequency, gainDb, q, sampleRate,
            out var b0, out var b1, out var b2, out var a1, out var a2);
        SetCoefficients(b0, b1, b2, a1, a2);
    }

    /// <summary>Drop the delay line — useful when re-targeting a freshly-allocated filter.</summary>
    public void Reset()
    {
        _z1 = 0f;
        _z2 = 0f;
    }

    /// <summary>Process one sample (Transposed Direct Form II).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Process(float x)
    {
        var y = _b0 * x + _z1;
        _z1 = _b1 * x - _a1 * y + _z2;
        _z2 = _b2 * x - _a2 * y;
        return y;
    }

    /// <summary>Process a block of samples in place.</summary>
    public void Process(Span<float> samples)
    {
        var b0 = _b0; var b1 = _b1; var b2 = _b2;
        var a1 = _a1; var a2 = _a2;
        var z1 = _z1; var z2 = _z2;
        for (var i = 0; i < samples.Length; i++)
        {
            var x = samples[i];
            var y = b0 * x + z1;
            z1 = b1 * x - a1 * y + z2;
            z2 = b2 * x - a2 * y;
            samples[i] = y;
        }
        _z1 = z1;
        _z2 = z2;
    }

    /// <summary>
    /// Evaluate this filter's magnitude response (linear, not dB) at the
    /// given frequency. Cheap closed-form derived from
    /// <c>|H(e^{jω})|</c>; useful for unit tests that verify a peaking band
    /// has the expected boost.
    /// </summary>
    public double MagnitudeAt(double frequency, int sampleRate)
        => MagnitudeAt(_b0, _b1, _b2, _a1, _a2, frequency, sampleRate);

    /// <summary>
    /// Static <c>|H(e^{jω})|</c> for a normalised biquad. Pulled out so the
    /// FE response curve and the BE tests share one definition.
    /// </summary>
    public static double MagnitudeAt(
        double b0, double b1, double b2,
        double a1, double a2,
        double frequency, int sampleRate)
    {
        var omega = 2.0 * Math.PI * frequency / sampleRate;
        var cosO  = Math.Cos(omega);
        var cos2O = Math.Cos(2.0 * omega);
        var sinO  = Math.Sin(omega);
        var sin2O = Math.Sin(2.0 * omega);

        var numRe = b0 + b1 * cosO + b2 * cos2O;
        var numIm =     -b1 * sinO - b2 * sin2O;
        var denRe = 1.0 + a1 * cosO + a2 * cos2O;
        var denIm =     -a1 * sinO - a2 * sin2O;

        var num2 = numRe * numRe + numIm * numIm;
        var den2 = denRe * denRe + denIm * denIm;
        if (den2 < 1e-30) return 0.0;
        return Math.Sqrt(num2 / den2);
    }

    /// <summary>
    /// RBJ cookbook (<see href="https://webaudio.github.io/Audio-EQ-Cookbook/audio-eq-cookbook.html"/>)
    /// coefficient computation. Returns the filter coefficients normalised
    /// against a0 (so the caller doesn't need to divide). Sample rate
    /// awareness is folded into <c>ω0 = 2π·f/Fs</c>.
    ///
    /// All shapes used by the EQ are covered:
    ///   - <see cref="EqBandType.Peaking"/>
    ///   - <see cref="EqBandType.LowShelf"/> / <see cref="EqBandType.HighShelf"/>
    ///   - <see cref="EqBandType.LowPass"/> / <see cref="EqBandType.HighPass"/>
    ///   - <see cref="EqBandType.Notch"/>
    /// </summary>
    public static void ComputeCoefficients(
        EqBandType type,
        float frequency,
        float gainDb,
        float q,
        int sampleRate,
        out float b0, out float b1, out float b2,
        out float a1, out float a2)
    {
        // Clamp inputs so we don't produce NaNs / infinities for nonsense
        // requests from the wire. The mix path can't recover from poisoned
        // coefficients without a state swap.
        var f = Math.Max(1f, Math.Min(frequency, sampleRate * 0.49f));
        var qv = Math.Max(0.05f, q);

        var omega = 2.0 * Math.PI * f / sampleRate;
        var sinO  = Math.Sin(omega);
        var cosO  = Math.Cos(omega);
        var alpha = sinO / (2.0 * qv);
        var A     = Math.Pow(10.0, gainDb / 40.0);

        double rb0, rb1, rb2, ra0, ra1, ra2;
        switch (type)
        {
            case EqBandType.Peaking:
                rb0 = 1.0 + alpha * A;
                rb1 = -2.0 * cosO;
                rb2 = 1.0 - alpha * A;
                ra0 = 1.0 + alpha / A;
                ra1 = -2.0 * cosO;
                ra2 = 1.0 - alpha / A;
                break;

            case EqBandType.LowShelf:
            {
                var sqrtA   = Math.Sqrt(A);
                var twoSqrtAAlpha = 2.0 * sqrtA * alpha;
                rb0 = A * ((A + 1.0) - (A - 1.0) * cosO + twoSqrtAAlpha);
                rb1 = 2.0 * A * ((A - 1.0) - (A + 1.0) * cosO);
                rb2 = A * ((A + 1.0) - (A - 1.0) * cosO - twoSqrtAAlpha);
                ra0 =      (A + 1.0) + (A - 1.0) * cosO + twoSqrtAAlpha;
                ra1 = -2.0 * ((A - 1.0) + (A + 1.0) * cosO);
                ra2 =      (A + 1.0) + (A - 1.0) * cosO - twoSqrtAAlpha;
                break;
            }

            case EqBandType.HighShelf:
            {
                var sqrtA   = Math.Sqrt(A);
                var twoSqrtAAlpha = 2.0 * sqrtA * alpha;
                rb0 =  A * ((A + 1.0) + (A - 1.0) * cosO + twoSqrtAAlpha);
                rb1 = -2.0 * A * ((A - 1.0) + (A + 1.0) * cosO);
                rb2 =  A * ((A + 1.0) + (A - 1.0) * cosO - twoSqrtAAlpha);
                ra0 =       (A + 1.0) - (A - 1.0) * cosO + twoSqrtAAlpha;
                ra1 =  2.0 * ((A - 1.0) - (A + 1.0) * cosO);
                ra2 =       (A + 1.0) - (A - 1.0) * cosO - twoSqrtAAlpha;
                break;
            }

            case EqBandType.LowPass:
                rb0 = (1.0 - cosO) * 0.5;
                rb1 =  1.0 - cosO;
                rb2 = (1.0 - cosO) * 0.5;
                ra0 =  1.0 + alpha;
                ra1 = -2.0 * cosO;
                ra2 =  1.0 - alpha;
                break;

            case EqBandType.HighPass:
                rb0 =  (1.0 + cosO) * 0.5;
                rb1 = -(1.0 + cosO);
                rb2 =  (1.0 + cosO) * 0.5;
                ra0 =  1.0 + alpha;
                ra1 = -2.0 * cosO;
                ra2 =  1.0 - alpha;
                break;

            case EqBandType.Notch:
                rb0 =  1.0;
                rb1 = -2.0 * cosO;
                rb2 =  1.0;
                ra0 =  1.0 + alpha;
                ra1 = -2.0 * cosO;
                ra2 =  1.0 - alpha;
                break;

            default:
                // Unknown shape — fall back to identity so the chain stays clean.
                rb0 = 1.0; rb1 = 0.0; rb2 = 0.0;
                ra0 = 1.0; ra1 = 0.0; ra2 = 0.0;
                break;
        }

        var inv = 1.0 / ra0;
        b0 = (float)(rb0 * inv);
        b1 = (float)(rb1 * inv);
        b2 = (float)(rb2 * inv);
        a1 = (float)(ra1 * inv);
        a2 = (float)(ra2 * inv);
    }
}
