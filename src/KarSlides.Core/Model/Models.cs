namespace KarSlides.Core.Model;

/// <summary>Slot life cycle. "Live" is a tally flag (M2), not a state.</summary>
public enum SlotState
{
    Empty,
    Loading,
    Ready,
    Unloading,
    Error,

    /// <summary>The watchdog is restarting PowerPoint; the slot comes back at the same position.</summary>
    Recovering,
}

/// <summary>vMix tally of the slot's NDI input. Unknown without vMix.</summary>
public readonly record struct Tally(bool OnProgram, bool OnPreview)
{
    public static readonly Tally None = new(false, false);
}

/// <summary>What the NDI output sends.</summary>
public enum OutputMode
{
    /// <summary>Newest captured frame.</summary>
    Live,

    /// <summary>Last frame repeated; new frames are ignored.</summary>
    Freeze,

    /// <summary>Black frame (only on explicit command).</summary>
    Black,
}

/// <summary>Position in a show: slide (1-based) and click = number of animation steps already played.</summary>
public readonly record struct ShowPosition(int Slide, int Click)
{
    public override string ToString() => $"{Slide}.{Click}";
}

/// <summary>
/// What PowerPoint reports. ClickIndex is not reliable after Previous. <see cref="Paused"/>: PowerPoint
/// paused the show itself – it does so when a document window opens in the same process and never resumes it
///. <see cref="OtherWindows"/>: document windows open in PowerPoint; KarSlides opens its presentations
/// without windows, so these are someone else's.
/// </summary>
public readonly record struct HostPosition(int Slide, int ClickIndex, int ClickCount, bool Running, bool Paused = false,
    int OtherWindows = 0);

/// <summary>A presentation opened in the host.</summary>
public sealed record SlotHandle(Guid Id, string Path, int SlideCount);

/// <summary>Physical placement of the monitor a show runs on, resolved right before each start.</summary>
public sealed record MonitorTarget(string DeviceName, int Left, int Top, int Width, int Height)
{
    public override string ToString() => $"{DeviceName} {Width}x{Height} at ({Left},{Top})";
}

/// <summary>Snapshot of a slot for the API.</summary>
public sealed record SlotStatus(
    int Slot,
    SlotState State,
    string? File,
    int Slide,
    int SlideCount,
    int Click,
    int ClickCount,
    OutputMode Output,
    bool Black,
    bool Freeze,
    string? Error,
    Preflight.PreflightReport? Preflight = null,
    int Thumbnails = 0,
    bool OnProgram = false,
    bool OnPreview = false);
