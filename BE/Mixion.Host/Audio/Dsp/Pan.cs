namespace Mixion.Host.Audio.Dsp;

/// <summary>
/// Constant-power pan law. Maps a position in <c>[-1, +1]</c> to a left /
/// right gain pair such that <c>L² + R² = 1</c> for all positions —
/// preserving total acoustic energy as the source moves across the
/// stereo field.
///
/// At the centre (<c>position = 0</c>), both legs sit at <c>1/√2 ≈ 0.707</c>
/// (the canonical -3 dB centre dip you'd see on a hardware desk). The
/// extremes go to (1, 0) and (0, 1).
///
/// The mix engine itself uses <see cref="ComputeBalance"/>: every bus is
/// stereo, so the "pan" control on a strip is a balance control with unity
/// on both sides at centre. <see cref="Compute"/> and <see cref="MonoGain"/>
/// remain for callers that want the constant-power law.
/// </summary>
public static class Pan
{
    /// <summary>π/2; one of two corner constants for the constant-power curve.</summary>
    private const double PiOverTwo = Math.PI * 0.5;

    /// <summary>
    /// Compute the L/R gain pair for <paramref name="position"/>. Inputs
    /// outside <c>[-1, +1]</c> are clamped.
    /// </summary>
    public static void Compute(float position, out float left, out float right)
    {
        var p = position;
        if (p > 1f)  p =  1f;
        if (p < -1f) p = -1f;

        // angle ∈ [0, π/2]; angle=0 → left = 1, angle=π/2 → right = 1.
        var angle = (p + 1f) * 0.25f * Math.PI;
        left  = (float)Math.Cos(angle);
        right = (float)Math.Sin(angle);
    }

    /// <summary>
    /// Mono-bus gain for the given pan position: <c>max(L, R)</c>. Full
    /// level (1.0) at either extreme, <c>1/√2 ≈ 0.707</c> at centre.
    /// </summary>
    public static float MonoGain(float position)
    {
        Compute(position, out var l, out var r);
        return l > r ? l : r;
    }

    /// <summary>
    /// Linear stereo-balance gains. <c>p = 0</c> → both sides at unity (no
    /// centre dip — feels right on a console fader). <c>p &gt; 0</c>
    /// attenuates the L side proportionally; <c>p &lt; 0</c> attenuates R.
    /// At the extremes, the off-side hits zero.
    /// </summary>
    public static void ComputeBalance(float position, out float gL, out float gR)
    {
        var p = position;
        if (p > 1f) p = 1f;
        if (p < -1f) p = -1f;
        if (p >= 0f)
        {
            gL = 1f - p;
            gR = 1f;
        }
        else
        {
            gL = 1f;
            gR = 1f + p;
        }
    }
}
