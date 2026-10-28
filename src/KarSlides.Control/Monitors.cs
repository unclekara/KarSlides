using KarSlides.Core.Monitors;

namespace KarSlides.Control;

/// <summary>Connected monitors, from the app (KarSlides.Capture); the API does not enumerate displays itself.</summary>
public sealed record MonitorSource(Func<IReadOnlyList<MonitorDescriptor>> Enumerate);

public sealed record MonitorChoice(string Value, string Name, string DeviceName, MonitorConnection Connection, int Width, int Height,
    int Left, int Top, bool IsVirtual, bool IsPrimary, bool Suitable, IReadOnlyList<string> Notes);

/// <summary>A monitor spec from the settings and the monitor it resolves to now (null: not connected).</summary>
public sealed record MonitorSpec(string Spec, string? DeviceName);

public sealed record MonitorsResponse(int Width, int Height, MonitorSpec Current, MonitorSpec Saved, IReadOnlyList<MonitorChoice> Monitors);

/// <summary>
/// Monitor list for the settings page: virtual, a dummy plug or a separate physical monitor can host the
/// show. A choice is stored as the full DeviceID (stable across reboots, unlike \\.\DISPLAYn).
/// </summary>
public static class MonitorChoices
{
    public static MonitorsResponse Build(IReadOnlyList<MonitorDescriptor> monitors, int width, int height, string currentSpec, string savedSpec,
        string? lang = null)
    {
        var en = SettingsCatalog.IsEnglish(lang);
        MonitorSpec Resolve(string spec) => new(spec, MonitorMatcher.Match(spec, monitors)?.DeviceName);

        var choices = monitors.OrderBy(m => m.Left).ThenBy(m => m.Top).Select(m =>
        {
            var notes = new List<string>();
            var sizeOk = m.Width == width && m.Height == height;
            if (!sizeOk) notes.Add(en
                ? $"Resolution {m.Width}×{m.Height}, {width}×{height} needed: change it in the Windows display settings"
                : $"Разрешение {m.Width}×{m.Height}, нужно {width}×{height}: поменяйте в параметрах экрана Windows");
            if (m.IsPrimary) notes.Add(en
                ? "Primary monitor: the show will cover the desktop and the taskbar"
                : "Основной монитор: показ закроет рабочий стол и панель задач");
            if (!m.IsVirtual && !m.IsPrimary)
                notes.Add(en
                    ? "Physical monitor or dummy plug: whoever watches this screen sees the show; the mouse may wander onto it"
                    : "Физический монитор или заглушка: зрители этого экрана увидят показ; мышь может заехать на него");
            if (string.IsNullOrEmpty(m.DeviceId)) notes.Add(en
                ? "Windows does not report a DeviceID: cannot be chosen"
                : "Windows не сообщает DeviceID: выбрать нельзя");
            return new MonitorChoice(m.DeviceId, DisplayName(m, lang), m.DeviceName, m.Connection, m.Width, m.Height, m.Left, m.Top,
                m.IsVirtual, m.IsPrimary, sizeOk && m.DeviceId.Length > 0, notes);
        }).ToArray();

        return new MonitorsResponse(width, height, Resolve(currentSpec), Resolve(savedSpec), choices);
    }

    /// <summary>EDID name, or what the monitor is, or the model part of the DeviceID (MONITOR\GBT3203\… → GBT3203).</summary>
    public static string DisplayName(MonitorDescriptor m, string? lang = null)
    {
        if (!string.IsNullOrWhiteSpace(m.FriendlyName)) return m.FriendlyName;
        if (m.Connection == MonitorConnection.Internal) return SettingsCatalog.IsEnglish(lang) ? "Built-in screen" : "Встроенный экран";
        var parts = m.DeviceId.Split('\\');
        return parts.Length > 1 ? parts[1] : m.DeviceName;
    }
}
