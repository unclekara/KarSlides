namespace KarSlides.Core.Slots;

/// <summary>
/// All slots of the application, addressed by 1-based index. Filled by the runtime after the start-up checks;
/// the API is up before that and simply sees no slots yet.
/// </summary>
public sealed class SlotSet
{
    private readonly object _sync = new();
    private readonly Dictionary<int, SlotController> _byIndex = new();

    public SlotSet()
    {
    }

    public SlotSet(IEnumerable<SlotController> slots)
    {
        foreach (var s in slots) _byIndex.Add(s.Index, s);
    }

    public IReadOnlyList<SlotController> All
    {
        get { lock (_sync) return _byIndex.Values.OrderBy(s => s.Index).ToArray(); }
    }

    public SlotController? Find(int index)
    {
        lock (_sync) return _byIndex.GetValueOrDefault(index);
    }

    public void Add(SlotController slot)
    {
        lock (_sync) _byIndex.Add(slot.Index, slot);
    }
}

/// <summary>Start-up environment problems (PowerPoint, NDI, monitor), shown in the tray and in /api/state.</summary>
public sealed class AppStatus
{
    private readonly object _sync = new();
    private readonly List<string> _problems = [];

    /// <summary>False until the runtime finished its start-up checks.</summary>
    public bool Started { get; set; }

    /// <summary>vMix connection state for /api/state (null when the integration is off).</summary>
    public Func<object?> Vmix { get; set; } = () => null;

    private readonly Dictionary<string, string> _warnings = new();

    /// <summary>Start-up problems plus current warnings (warnings can clear themselves).</summary>
    public IReadOnlyList<string> Problems
    {
        get { lock (_sync) return _problems.Concat(_warnings.Values).ToArray(); }
    }

    public void AddProblem(string problem)
    {
        lock (_sync) _problems.Add(problem);
    }

    /// <summary>Sets (or clears with null) a warning that may change while running, e.g. audio enhancements.</summary>
    public void SetWarning(string key, string? message)
    {
        lock (_sync)
        {
            if (message is null) _warnings.Remove(key);
            else _warnings[key] = message;
        }
    }
}
