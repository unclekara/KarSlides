namespace KarSlides.Core.Configuration;

/// <summary>Root of appsettings.json / %APPDATA%\KarSlides\settings.json (M1 subset).</summary>
public sealed class KarSlidesOptions
{
    public OutputOptions Output { get; set; } = new();
    public SlotsOptions Slots { get; set; } = new();
    public ControlOptions Control { get; set; } = new();
    public WatchdogOptions Watchdog { get; set; } = new();
    public ShowOptions Show { get; set; } = new();
    public PreviewSettings Preview { get; set; } = new();
    public LoggingOptions Logging { get; set; } = new();
    public AudioOptions Audio { get; set; } = new();
    public PathsOptions Paths { get; set; } = new();
    public PreflightOptions Preflight { get; set; } = new();
    public VmixOptions Vmix { get; set; } = new();
    public ClickerOptions Clicker { get; set; } = new();
}

/// <summary>Presenter clicker through a global keyboard hook. Key names as in .NET Keys.</summary>
public sealed class ClickerOptions
{
    public bool Enabled { get; set; } = true;

    // Only PageUp/PageDown by default: letters, Space and arrows would fire while the operator
    // types in vMix. Extra keys (e.g. "Right", "Space", "B") can be added in settings.json.
    public List<string> Next { get; set; } = ["PageDown"];
    public List<string> Prev { get; set; } = ["PageUp"];
    public List<string> Black { get; set; } = [];

    /// <summary>Keys that do not reach other applications (vMix) while the clicker is on.</summary>
    public List<string> Swallow { get; set; } = ["PageDown", "PageUp"];
}

public sealed class VmixOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// "auto" tries [::1] then 127.0.0.1 and keeps the one where vMix answers (127.0.0.1:8088 can belong to
    /// WSL); or an explicit host/IP.
    /// </summary>
    public string Host { get; set; } = "auto";

    public int TcpPort { get; set; } = 8099;

    /// <summary>
    /// vMix Web Controller port (vMix Settings -> Web Controller). 0 = find it automatically among the ports the
    /// local vMix process listens on (works whatever port vMix uses).
    /// </summary>
    public int HttpPort { get; set; }

    /// <summary>How often the input list is re-read to map slots to NDI inputs.</summary>
    public int InputRefreshSeconds { get; set; } = 5;
}

public sealed class PathsOptions
{
    /// <summary>Local copies of loaded presentations and their thumbnails. Environment variables are expanded.</summary>
    public string Cache { get; set; } = @"%LOCALAPPDATA%\KarSlides\cache";

    public string CacheExpanded => Environment.ExpandEnvironmentVariables(Cache);

    /// <summary>Folder with presentations offered by the operator panel (GET /api/files).</summary>
    public string Inbox { get; set; } = @"C:\KarSlides\inbox";

    public string InboxExpanded => Environment.ExpandEnvironmentVariables(Inbox);
}

public sealed class PreflightOptions
{
    /// <summary>Export thumbnails of all slides in the background after load.</summary>
    public bool Thumbnails { get; set; } = true;
}

public sealed class AudioOptions
{
    /// <summary>Sound of POWERPNT.EXE (embedded videos) in the NDI stream of the audio owner slot (slot 1 until M3).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Pre-roll after the ring ran dry; latency itself is trimmed to ~5 ms of safety margin.</summary>
    public double BufferMs { get; set; } = 20;

    /// <summary>
    /// Where PowerPoint plays video sound locally: "" – the Windows default, or an MMDevice id
    /// ({0.0.0.00000000}.{guid}). NDI gets the sound either way.
    /// </summary>
    public string OutputDevice { get; set; } = "";
}

public sealed class LoggingOptions
{
    /// <summary>Verbose, Debug, Information, Warning, Error. Debug logs every COM call with timings.</summary>
    public string Level { get; set; } = "Information";

    public int RetainedDays { get; set; } = 14;
}

public sealed class OutputOptions
{
    /// <summary>Fixed 1920x1080: the slot monitor must have this resolution, no scaling.</summary>
    public int Width { get; set; } = 1920;

    public int Height { get; set; } = 1080;

    /// <summary>25, 29.97, 30, 50, 59.94 or 60.</summary>
    public string Fps { get; set; } = "50";

    /// <summary>"async" (default) or "sync".</summary>
    public string Send { get; set; } = "async";

    /// <summary>See <see cref="Slots.SlotOptions.HoldRelease"/>.</summary>
    public int HoldReleaseMs { get; set; } = 200;

    /// <summary>"capture" (the slot monitor) or "testpattern" (check NDI -> vMix without PowerPoint).</summary>
    public string Source { get; set; } = "capture";
}

public sealed class SlotsOptions
{
    /// <summary>M1: exactly one slot is supported.</summary>
    public int Count { get; set; } = 1;

    public string NdiNameTemplate { get; set; } = "KarSlides - Slot {n}";

    /// <summary>Monitor per slot, see <see cref="Monitors.MonitorMatcher"/>: "virtual", "virtual:2", DeviceID part or \\.\DISPLAYn.</summary>
    public List<string> Monitors { get; set; } = ["virtual"];

    /// <summary>How long to wait at start-up for the slot monitor to appear (IddCx drivers come up late).</summary>
    public int MonitorWaitSeconds { get; set; } = 30;
}

public sealed class ControlOptions
{
    /// <summary>Not 8088/8099 (vMix).</summary>
    public int Port { get; set; } = 9180;

    public string Bind { get; set; } = "0.0.0.0";

    /// <summary>If set, required in header "X-KarSlides-Token" or query "?token=".</summary>
    public string? Token { get; set; }
}

public sealed class WatchdogOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Timeout of ordinary COM calls (commands).</summary>
    public int ComTimeoutMs { get; set; } = 3000;

    public int HeartbeatMs { get; set; } = 1000;

    /// <summary>A heartbeat that takes longer is missed.</summary>
    public int HeartbeatTimeoutMs { get; set; } = 1000;

    /// <summary>Missed heartbeats in a row before PowerPoint is restarted.</summary>
    public int MissedLimit { get; set; } = 3;

    /// <summary>
    /// Restart a show PowerPoint paused (a document was opened in it) at the same position once the
    /// document is closed; the output freezes for the restart. Off: only a warning.
    /// </summary>
    public bool RestartPausedShow { get; set; }

    /// <summary>No second automatic restart of the same slot sooner than this.</summary>
    public int PausedRestartIntervalMs { get; set; } = 30000;
}

public sealed class ShowOptions
{
    /// <summary>"both", "restore", "noactivate" or "none".</summary>
    public string Focus { get; set; } = "both";

    /// <summary>Show window always on top of other windows on its monitor.</summary>
    public bool Topmost { get; set; } = true;
}

/// <summary>Live picture in the web UI: MJPEG of what goes on air.</summary>
public sealed class PreviewSettings
{
    public bool Live { get; set; } = true;
    public double Fps { get; set; } = 25;
    public int Width { get; set; } = 960;
    public int Quality { get; set; } = 75;
}
