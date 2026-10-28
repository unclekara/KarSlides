namespace KarSlides.Core.Monitors;

/// <summary>A monitor as enumerated by Windows (filled by KarSlides.Capture).</summary>
public sealed record MonitorDescriptor(
    string DeviceName,   // \\.\DISPLAY5 — changes between boots for IddCx monitors
    string DeviceId,     // MONITOR\MTT1337\{...} — stable
    string AdapterName,
    bool IsVirtual,
    bool IsPrimary,
    int Left,
    int Top,
    int Width,
    int Height,
    string FriendlyName = "", // from EDID, e.g. "M32Q"; empty if Windows does not know it
    MonitorConnection Connection = MonitorConnection.Unknown);

/// <summary>How the monitor is attached (DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY), for the settings page.</summary>
public enum MonitorConnection { Unknown, Virtual, Internal, Hdmi, DisplayPort, Dvi, Vga, Wireless, Other }

/// <summary>
/// Resolves a monitor spec from the config to the current monitor. Specs:
/// <list type="bullet">
/// <item><c>virtual</c> / <c>virtual:2</c> — first / second virtual monitor (by position, left to right);</item>
/// <item><c>\\.\DISPLAY5</c> — exact device name (not stable for virtual monitors);</item>
/// <item>anything else — case-insensitive substring of the monitor DeviceID, e.g. <c>MTT1337</c> (leftmost if several).</item>
/// </list>
/// </summary>
public static class MonitorMatcher
{
    public static MonitorDescriptor? Match(string spec, IReadOnlyList<MonitorDescriptor> monitors)
    {
        spec = spec.Trim();
        if (spec.Length == 0) return null;

        if (spec.StartsWith("virtual", StringComparison.OrdinalIgnoreCase))
        {
            var n = 1;
            var colon = spec.IndexOf(':');
            if (colon >= 0 && (!int.TryParse(spec[(colon + 1)..], out n) || n < 1)) return null;
            if (colon < 0 && spec.Length != "virtual".Length) return null;
            return monitors.Where(m => m.IsVirtual).OrderBy(m => m.Left).ThenBy(m => m.Top).Skip(n - 1).FirstOrDefault();
        }

        if (spec.StartsWith(@"\\.\", StringComparison.Ordinal))
            return monitors.FirstOrDefault(m => m.DeviceName.Equals(spec, StringComparison.OrdinalIgnoreCase));

        // Several monitors of one driver share the model part of the DeviceID: take the leftmost, deterministically.
        return monitors.Where(m => m.DeviceId.Contains(spec, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.Left).ThenBy(m => m.Top).FirstOrDefault();
    }
}
