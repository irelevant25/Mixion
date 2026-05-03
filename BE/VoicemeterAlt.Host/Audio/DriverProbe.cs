namespace VoicemeterAlt.Host.Audio;

public sealed record DriverProbeResult(bool Found, AudioEndpoint? MatchedDevice);

/// <summary>
/// Detects whether the **basic** single-cable VB-CABLE is installed —
/// the free product that exposes exactly one render endpoint named
/// <c>"CABLE Input (VB-Audio Virtual Cable)"</c> and one capture endpoint named
/// <c>"CABLE Output (VB-Audio Virtual Cable)"</c>.
///
/// <para>
/// VB-Audio also sells higher-tier products (VB-CABLE A+B, VB-CABLE C+D, the
/// Voicemeeter family) whose endpoints use distinct prefixes — <c>CABLE-A</c>,
/// <c>CABLE-B</c>, <c>VAIO</c>, <c>HiFi-Cable</c>, etc. Those installs do not
/// satisfy this app's requirement: we want exactly the basic cable so the
/// "single fake mic" path stays predictable for the user. The matcher
/// therefore anchors at the start of the friendly name and treats
/// <c>CABLE-A Input</c> / <c>CABLE-B Output</c> / similar as no match.
/// </para>
/// </summary>
public static class DriverProbe
{
    /// <summary>
    /// Friendly-name prefixes that identify the basic VB-CABLE product. The
    /// match is case-insensitive but must occur at the start of the name and
    /// be followed by whitespace or the opening parenthesis of the
    /// driver-supplied suffix — that's what excludes the hyphenated A/B/C/D
    /// variants from higher-tier products.
    /// </summary>
    private static readonly string[] BasicPrefixes =
    {
        "CABLE Input",
        "CABLE Output",
    };

    public static DriverProbeResult Probe(IEndpointSource source)
    {
        foreach (var ep in source.Capture().Concat(source.Render()))
        {
            if (IsBasicVbCable(ep))
                return new DriverProbeResult(true, ep);
        }
        return new DriverProbeResult(false, null);
    }

    private static bool IsBasicVbCable(AudioEndpoint ep)
    {
        var name = ep.FriendlyName?.TrimStart() ?? string.Empty;
        foreach (var prefix in BasicPrefixes)
        {
            if (StartsWithWholeWord(name, prefix)) return true;
        }
        return false;
    }

    /// <summary>
    /// True when <paramref name="name"/> begins with <paramref name="prefix"/>
    /// (case-insensitive) AND the character after the prefix is either
    /// end-of-string, whitespace, or <c>'('</c>. Without the boundary check,
    /// a prefix like <c>"CABLE Input"</c> would also match
    /// <c>"CABLE InputX"</c>; with it we can match
    /// <c>"CABLE Input (VB-Audio Virtual Cable)"</c> and reject
    /// <c>"CABLE-A Input ..."</c> at the same time.
    /// </summary>
    private static bool StartsWithWholeWord(string name, string prefix)
    {
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        if (name.Length == prefix.Length) return true;
        var next = name[prefix.Length];
        return char.IsWhiteSpace(next) || next == '(';
    }
}
