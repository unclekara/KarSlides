using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace KarSlides.PowerPoint;

/// <summary>Access to HKCU\Software\Microsoft\Office\{ver}\PowerPoint\Options values (null = absent).</summary>
public interface IPowerPointOptionsStore
{
    string? GetString(string name);
    void SetString(string name, string? value);
    int? GetDword(string name);
    void SetDword(string name, int? value);
}

internal sealed class RegistryOptionsStore(string officeVersion) : IPowerPointOptionsStore
{
    private readonly string _path = $@"Software\Microsoft\Office\{officeVersion}\PowerPoint\Options";

    public string? GetString(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(_path);
        return key?.GetValue(name) as string;
    }

    public void SetString(string name, string? value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_path, writable: true);
        if (value is null) key.DeleteValue(name, throwOnMissingValue: false);
        else key.SetValue(name, value, RegistryValueKind.String);
    }

    public int? GetDword(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(_path);
        return key?.GetValue(name) as int?;
    }

    public void SetDword(string name, int? value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_path, writable: true);
        if (value is null) key.DeleteValue(name, throwOnMissingValue: false);
        else key.SetValue(name, value.Value, RegistryValueKind.DWord);
    }
}

/// <summary>
/// Puts the slide show on a monitor through PowerPoint's own settings: DisplayMonitor plus
/// UseAutoMonSelection=0 (otherwise DisplayMonitor is ignored). The user's values are saved to a file before
/// the first change and restored right after Run(); if the process dies in between, the next start restores
/// them (<see cref="Restore"/> is idempotent). The file is never overwritten while it exists, so a second
/// change cannot record already modified values as "original".
/// </summary>
public sealed class MonitorSettingsGuard(IPowerPointOptionsStore store, string backupFile, ILogger log)
{
    internal const string DisplayMonitor = "DisplayMonitor";
    internal const string UseAutoMonSelection = "UseAutoMonSelection";

    private sealed record Backup(string? DisplayMonitor, int? UseAutoMonSelection);

    public void Apply(string deviceName)
    {
        if (!File.Exists(backupFile))
        {
            var original = new Backup(store.GetString(DisplayMonitor), store.GetDword(UseAutoMonSelection));
            Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
            File.WriteAllText(backupFile, JsonSerializer.Serialize(original));
            log.LogDebug("PowerPoint monitor settings saved: DisplayMonitor='{Display}', UseAutoMonSelection={Auto}",
                original.DisplayMonitor, original.UseAutoMonSelection);
        }
        store.SetString(DisplayMonitor, deviceName);
        store.SetDword(UseAutoMonSelection, 0);
    }

    /// <summary>Restores the saved user values if a backup exists. Returns true if something was restored.</summary>
    public bool Restore()
    {
        if (!File.Exists(backupFile)) return false;
        Backup? original;
        try
        {
            original = JsonSerializer.Deserialize<Backup>(File.ReadAllText(backupFile));
        }
        catch (JsonException ex)
        {
            log.LogError(ex, "Backup of PowerPoint monitor settings '{File}' is damaged, leaving the registry as is", backupFile);
            File.Delete(backupFile);
            return false;
        }

        store.SetString(DisplayMonitor, original?.DisplayMonitor);
        store.SetDword(UseAutoMonSelection, original?.UseAutoMonSelection);
        File.Delete(backupFile);
        log.LogDebug("PowerPoint monitor settings restored: DisplayMonitor='{Display}', UseAutoMonSelection={Auto}",
            original?.DisplayMonitor, original?.UseAutoMonSelection);
        return true;
    }
}
