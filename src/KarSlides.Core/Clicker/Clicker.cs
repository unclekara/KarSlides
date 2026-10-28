using KarSlides.Core.Configuration;
using KarSlides.Core.Slots;
using Microsoft.Extensions.Logging;

namespace KarSlides.Core.Clicker;

public enum ClickerAction
{
    Next,
    Previous,
    ToggleBlack,
}

/// <summary>What the keyboard hook does with one key event.</summary>
public readonly record struct ClickerDecision(ClickerAction? Action, bool Swallow)
{
    public static readonly ClickerDecision PassThrough = new(null, false);
}

/// <summary>
/// Key -> action map of the clicker, independent of Windows: the hook passes virtual-key codes.
/// Acts on the key-down transition only (auto-repeat of a held key is ignored); keys with Ctrl/Alt/Win are
/// never touched, so shortcuts in vMix keep working. Swallowed keys are swallowed on down, repeat and up.
/// </summary>
public sealed class ClickerKeymap
{
    private readonly Dictionary<int, ClickerAction> _actions = new();
    private readonly HashSet<int> _swallow = new();
    private readonly HashSet<int> _down = new();

    public ClickerKeymap(ClickerOptions options)
    {
        Add(options.Next, ClickerAction.Next);
        Add(options.Prev, ClickerAction.Previous);
        Add(options.Black, ClickerAction.ToggleBlack);
        foreach (var name in options.Swallow) _swallow.Add(VirtualKeys.Parse(name));
    }

    private void Add(IEnumerable<string> names, ClickerAction action)
    {
        foreach (var name in names) _actions[VirtualKeys.Parse(name)] = action;
    }

    public ClickerDecision Decide(int vk, bool keyDown, bool modifiers)
    {
        if (!_actions.TryGetValue(vk, out var action)) return ClickerDecision.PassThrough;
        if (!keyDown)
        {
            var wasDown = _down.Remove(vk);
            return new ClickerDecision(null, wasDown && _swallow.Contains(vk));
        }
        if (modifiers) return ClickerDecision.PassThrough;

        var first = _down.Add(vk);
        return new ClickerDecision(first ? action : null, _swallow.Contains(vk));
    }
}

/// <summary>Virtual-key codes by .NET Keys names (the subset that makes sense for a clicker).</summary>
public static class VirtualKeys
{
    private static readonly Dictionary<string, int> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Back"] = 0x08, ["Tab"] = 0x09, ["Enter"] = 0x0D, ["Return"] = 0x0D, ["Escape"] = 0x1B, ["Space"] = 0x20,
        ["PageUp"] = 0x21, ["Prior"] = 0x21, ["PageDown"] = 0x22, ["Next"] = 0x22, ["End"] = 0x23, ["Home"] = 0x24,
        ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28,
        ["OemPeriod"] = 0xBE, ["OemComma"] = 0xBC, ["OemMinus"] = 0xBD, ["Oemplus"] = 0xBB,
        ["MediaNextTrack"] = 0xB0, ["MediaPreviousTrack"] = 0xB1, ["MediaPlayPause"] = 0xB3,
        ["BrowserBack"] = 0xA6, ["BrowserForward"] = 0xA7,
    };

    public static int Parse(string name)
    {
        name = name.Trim();
        if (Named.TryGetValue(name, out var vk)) return vk;
        if (name.Length == 1 && char.IsAsciiLetterOrDigit(name[0])) return char.ToUpperInvariant(name[0]);
        if (name.Length == 2 && name[0] == 'D' && char.IsAsciiDigit(name[1])) return name[1];
        if ((name[0] is 'F' or 'f') && int.TryParse(name[1..], out var f) && f is >= 1 and <= 24) return 0x70 + f - 1;
        throw new FormatException($"Unknown clicker key '{name}'");
    }
}

/// <summary>
/// Clicker state and routing: on/off (API and UI), target slot (the one on program, else slot 1) and the
/// command queue. The hook calls <see cref="Handle"/> on its thread and must return within milliseconds,
/// so commands are queued and executed in order on the thread pool.
/// </summary>
public sealed class ClickerController
{
    private readonly ClickerKeymap _keymap;
    private readonly SlotSet _slots;
    private readonly ILogger _log;
    private readonly ClickerOptions _options;
    private readonly object _queueSync = new();
    private Task _tail = Task.CompletedTask;
    private volatile bool _enabled;

    public ClickerController(ClickerOptions options, SlotSet slots, ILogger log)
    {
        _options = options;
        _keymap = new ClickerKeymap(options);
        _slots = slots;
        _log = log;
        _enabled = options.Enabled;
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            _log.LogInformation("Clicker {State}", value ? "on" : "off");
            EnabledChanged?.Invoke(value);
        }
    }

    public event Action<bool>? EnabledChanged;

    public ClickerOptions Keys => _options;

    /// <summary>Called by the hook. Returns true if the key must not reach other applications.</summary>
    public bool Handle(int vk, bool keyDown, bool modifiers)
    {
        if (!_enabled) return false;
        var decision = _keymap.Decide(vk, keyDown, modifiers);
        if (decision.Action is { } action) Enqueue(action);
        return decision.Swallow;
    }

    /// <summary>The slot the clicker drives: the one on program in vMix, otherwise the first slot.</summary>
    public SlotController? Target()
    {
        var all = _slots.All;
        return all.FirstOrDefault(s => s.Status.OnProgram) ?? all.FirstOrDefault();
    }

    /// <summary>Completes when every queued command has run (tests).</summary>
    public Task Drain()
    {
        lock (_queueSync) return _tail;
    }

    private void Enqueue(ClickerAction action)
    {
        lock (_queueSync)
        {
            _tail = _tail.ContinueWith(_ => RunAsync(action), TaskScheduler.Default).Unwrap();
        }
    }

    private async Task RunAsync(ClickerAction action)
    {
        var slot = Target();
        if (slot is null) return;
        try
        {
            switch (action)
            {
                case ClickerAction.Next: await slot.NextAsync(CancellationToken.None); break;
                case ClickerAction.Previous: await slot.PreviousAsync(CancellationToken.None); break;
                case ClickerAction.ToggleBlack: slot.SetBlack(!slot.Status.Black); break;
            }
            _log.LogDebug("Clicker {Action} -> slot {Slot}", action, slot.Index);
        }
        catch (Exception ex)
        {
            // A clicker press on an empty or busy slot is not an error worth more than a line.
            _log.LogInformation("Clicker {Action} on slot {Slot} ignored: {Message}", action, slot.Index, ex.Message);
        }
    }
}
