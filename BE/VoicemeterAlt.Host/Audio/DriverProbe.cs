namespace VoicemeterAlt.Host.Audio;

public sealed record DriverProbeResult(bool Found, AudioEndpoint? MatchedDevice);

/// <summary>
/// Detects whether VB-CABLE (or any VB-Audio virtual cable) is present among
/// active WASAPI endpoints. Friendly-name match is case-insensitive against
/// "VB-Audio", "CABLE Input", "CABLE Output". Either direction qualifies — if
/// the OS has surfaced any of those endpoints, the driver is installed.
/// </summary>
public static class DriverProbe
{
    private static readonly string[] Needles =
    {
        "VB-Audio",
        "CABLE Input",
        "CABLE Output",
    };

    public static DriverProbeResult Probe(IEndpointSource source)
    {
        foreach (var ep in source.Capture().Concat(source.Render()))
        {
            if (IsVbCable(ep))
                return new DriverProbeResult(true, ep);
        }
        return new DriverProbeResult(false, null);
    }

    private static bool IsVbCable(AudioEndpoint ep)
    {
        foreach (var needle in Needles)
        {
            if (ep.FriendlyName.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                ep.InterfaceName.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
