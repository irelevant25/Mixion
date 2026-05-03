namespace VoicemeterAlt.Host.State;

/// <summary>
/// Live view of which preset (if any) the host most recently loaded or saved,
/// plus the resolved slot layout the FE should render. Held as a singleton so
/// the session endpoint can hand the FE the same layout the BE auto-applied
/// on startup. Mutations come from the preset RPC handlers and from the
/// startup auto-load path.
///
/// Kept threadsafe via a single private lock — updates are infrequent
/// (user-driven) so contention is a non-issue.
/// </summary>
public sealed class CurrentPresetState
{
    private readonly object _gate = new();
    private string? _name;
    private ResolvedSlot[] _inputSlots  = Array.Empty<ResolvedSlot>();
    private ResolvedSlot[] _outputSlots = Array.Empty<ResolvedSlot>();

    public string? Name
    {
        get { lock (_gate) return _name; }
    }

    public ResolvedSlot[] InputSlots
    {
        get { lock (_gate) return _inputSlots; }
    }

    public ResolvedSlot[] OutputSlots
    {
        get { lock (_gate) return _outputSlots; }
    }

    public void Set(string name, ResolvedSlot[] inputSlots, ResolvedSlot[] outputSlots)
    {
        lock (_gate)
        {
            _name        = name;
            _inputSlots  = inputSlots  ?? Array.Empty<ResolvedSlot>();
            _outputSlots = outputSlots ?? Array.Empty<ResolvedSlot>();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _name        = null;
            _inputSlots  = Array.Empty<ResolvedSlot>();
            _outputSlots = Array.Empty<ResolvedSlot>();
        }
    }
}
