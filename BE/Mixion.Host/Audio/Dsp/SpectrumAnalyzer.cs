namespace Mixion.Host.Audio.Dsp;

/// <summary>
/// Real-input magnitude spectrum analyzer for the EQ overlay (BE-081).
///
/// Fixed 1024-point Hann-windowed radix-2 FFT — small enough that one pass
/// runs in under a millisecond even on the broadcaster's general-purpose
/// thread, and large enough (≈47 Hz bin width @ 48 kHz) to read the
/// speech / music spectrum on a log-frequency canvas.
///
/// The class itself is stateless. Callers pre-allocate scratch buffers so
/// the broadcaster's hot loop never hits the GC.
/// </summary>
public static class SpectrumAnalyzer
{
    /// <summary>FFT length. Must be a power of two.</summary>
    public const int FftSize  = 1024;

    /// <summary>Number of single-sided magnitude bins emitted (DC … Nyquist-1).</summary>
    public const int BinCount = FftSize / 2;

    private static readonly float[] HannWindow = BuildHann(FftSize);

    /// <summary>
    /// Pre-computed coherent gain of the Hann window (≈0.5). Pulled out so
    /// the per-bin scale doesn't recompute it on every call.
    /// </summary>
    private const float HannCoherentGain = 0.5f;

    private static float[] BuildHann(int n)
    {
        var w = new float[n];
        for (var i = 0; i < n; i++)
            w[i] = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / (n - 1)));
        return w;
    }

    /// <summary>
    /// Compute the single-sided magnitude spectrum (in dBFS) of the most
    /// recent <see cref="FftSize"/> samples. Inputs and scratch buffers
    /// must each be at least <see cref="FftSize"/> long; the output must
    /// hold at least <see cref="BinCount"/> floats.
    /// </summary>
    public static void ComputeMagnitudeDb(
        ReadOnlySpan<float> samples,
        Span<float>         magnitudeDb,
        Span<float>         realScratch,
        Span<float>         imagScratch)
    {
        if (samples.Length     < FftSize)
            throw new ArgumentException($"samples must contain >= {FftSize} values", nameof(samples));
        if (magnitudeDb.Length < BinCount)
            throw new ArgumentException($"magnitudeDb must contain >= {BinCount} values", nameof(magnitudeDb));
        if (realScratch.Length < FftSize)
            throw new ArgumentException($"realScratch must contain >= {FftSize} values", nameof(realScratch));
        if (imagScratch.Length < FftSize)
            throw new ArgumentException($"imagScratch must contain >= {FftSize} values", nameof(imagScratch));

        // Windowed copy into the (real, imag) buffers. imag is zero — real
        // signal in.
        for (var i = 0; i < FftSize; i++)
        {
            realScratch[i] = samples[i] * HannWindow[i];
            imagScratch[i] = 0f;
        }

        Fft(realScratch, imagScratch);

        // Per-bin scale combines: 1/N (FFT normalisation) and 1/coherentGain
        // (so a full-scale tone reads at unity in the bin it lands on);
        // single-sided spectra also get ×2 on every bin except DC.
        var scale = 1f / (FftSize * HannCoherentGain);
        for (var k = 0; k < BinCount; k++)
        {
            var re  = realScratch[k];
            var im  = imagScratch[k];
            var mag = MathF.Sqrt(re * re + im * im) * scale;
            if (k > 0) mag *= 2f;
            magnitudeDb[k] = 20f * MathF.Log10(MathF.Max(1e-9f, mag));
        }
    }

    /// <summary>In-place radix-2 Cooley-Tukey FFT. Length must be a power of two.</summary>
    private static void Fft(Span<float> re, Span<float> im)
    {
        var n = re.Length;
        // Bit reversal.
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        // Cooley-Tukey butterflies.
        for (var size = 2; size <= n; size <<= 1)
        {
            var halfsize = size >> 1;
            var theta    = -2.0 * Math.PI / size;
            var cosTheta = (float)Math.Cos(theta);
            var sinTheta = (float)Math.Sin(theta);
            for (var i = 0; i < n; i += size)
            {
                var wRe = 1f;
                var wIm = 0f;
                for (var k = 0; k < halfsize; k++)
                {
                    var iA = i + k;
                    var iB = iA + halfsize;
                    var tRe = wRe * re[iB] - wIm * im[iB];
                    var tIm = wRe * im[iB] + wIm * re[iB];
                    re[iB]  = re[iA] - tRe;
                    im[iB]  = im[iA] - tIm;
                    re[iA] += tRe;
                    im[iA] += tIm;
                    var newWRe = wRe * cosTheta - wIm * sinTheta;
                    var newWIm = wRe * sinTheta + wIm * cosTheta;
                    wRe = newWRe;
                    wIm = newWIm;
                }
            }
        }
    }
}
