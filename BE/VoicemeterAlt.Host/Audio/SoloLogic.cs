using System.Collections.Immutable;
using VoicemeterAlt.Host.State;

namespace VoicemeterAlt.Host.Audio;

/// <summary>
/// Pure helpers for the solo / mute decision. Extracted so the audio loop
/// and the unit tests share one definition. The convention is "solo within
/// the bus": if any channel in a bus is soloed, every non-soloed channel in
/// that bus is silenced. Solo state in one bus does not affect the other.
/// </summary>
public static class SoloLogic
{
    /// <summary>True if at least one channel in the bus is soloed.</summary>
    public static bool HasAnySolo(ImmutableArray<Channel> bus)
    {
        for (var i = 0; i < bus.Length; i++)
            if (bus[i].Soloed) return true;
        return false;
    }

    /// <summary>True if at least one channel in the bus is soloed.</summary>
    public static bool HasAnySolo(IReadOnlyList<Channel> bus)
    {
        for (var i = 0; i < bus.Count; i++)
            if (bus[i].Soloed) return true;
        return false;
    }

    /// <summary>
    /// Decide whether <paramref name="channel"/> should pass audio given
    /// whether any channel in its bus is soloed. Mute always wins; solo only
    /// silences non-soloed channels in the same bus.
    /// </summary>
    public static bool IsAudible(Channel channel, bool anyInBusSoloed)
    {
        if (channel.Muted) return false;
        if (anyInBusSoloed && !channel.Soloed) return false;
        return true;
    }

    /// <summary>
    /// Convenience: <see cref="IsAudible(Channel, bool)"/> for a whole bus.
    /// Returns one bool per channel in <paramref name="bus"/>.
    /// </summary>
    public static bool[] AudibilityMask(ImmutableArray<Channel> bus)
    {
        var any = HasAnySolo(bus);
        var mask = new bool[bus.Length];
        for (var i = 0; i < bus.Length; i++)
            mask[i] = IsAudible(bus[i], any);
        return mask;
    }
}
