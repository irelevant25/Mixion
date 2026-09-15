namespace Mixion.Host.Audio;

/// <summary>
/// Apps the user detached from the mixer (the × next to an app in the slot
/// picker). They stay out of automatic discovery for the rest of the session,
/// so the device watcher doesn't attach them again a second later. A manual
/// rescan clears the list, and loading a preset that uses an app lifts its
/// entry. Names compare case-insensitively, as Windows process names do.
/// </summary>
public sealed class ProcessSuppressions
{
    private readonly object _gate = new();
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);

    public void Add(string processName)
    {
        lock (_gate) _names.Add(processName);
    }

    public void Remove(string processName)
    {
        lock (_gate) _names.Remove(processName);
    }

    public void Clear()
    {
        lock (_gate) _names.Clear();
    }

    public bool Contains(string processName)
    {
        lock (_gate) return _names.Contains(processName);
    }
}
