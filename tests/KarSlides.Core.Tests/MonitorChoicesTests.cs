using System.Text.Json;
using KarSlides.Control;
using KarSlides.Core.Monitors;

namespace KarSlides.Core.Tests;

public sealed class MonitorChoicesTests
{
    private static readonly MonitorDescriptor Main = new(@"\\.\DISPLAY2", @"MONITOR\GBT3203\{x}\0004", "NVIDIA", false, true,
        0, 0, 2560, 1440, "Gigabyte M32Q", MonitorConnection.Hdmi);
    private static readonly MonitorDescriptor Laptop = new(@"\\.\DISPLAY1", @"MONITOR\AUOB69B\{x}\0001", "Intel", false, false,
        322, 1440, 1920, 1080, "", MonitorConnection.Internal);
    private static readonly MonitorDescriptor Vdd = new(@"\\.\DISPLAY5", @"MONITOR\MTT1337\{x}\0020", "Virtual Display", true, false,
        2560, 0, 1920, 1080, "VDD by MTT", MonitorConnection.Virtual);
    private static readonly MonitorDescriptor Dummy = new(@"\\.\DISPLAY7", @"MONITOR\FUS0001\{x}\0007", "NVIDIA", false, false,
        4480, 0, 1920, 1080, "HDMI Dummy", MonitorConnection.Hdmi);

    [Fact]
    public void Build_MarksSuitability_AndResolvesSpecs()
    {
        var r = MonitorChoices.Build([Vdd, Main, Laptop, Dummy], 1920, 1080, "virtual", Dummy.DeviceId);

        Assert.Equal(@"\\.\DISPLAY5", r.Current.DeviceName);
        Assert.Equal(@"\\.\DISPLAY7", r.Saved.DeviceName);
        var main = r.Monitors.Single(m => m.DeviceName == Main.DeviceName);
        Assert.False(main.Suitable); // 2560x1440
        Assert.Contains(main.Notes, n => n.Contains("Основной"));
        var dummy = r.Monitors.Single(m => m.DeviceName == Dummy.DeviceName);
        Assert.True(dummy.Suitable);
        Assert.Contains(dummy.Notes, n => n.Contains("заглушка"));
        Assert.True(r.Monitors.Single(m => m.IsVirtual).Suitable);
        Assert.Equal(["Gigabyte M32Q", "Встроенный экран", "VDD by MTT", "HDMI Dummy"], r.Monitors.Select(m => m.Name)); // left to right
    }

    [Fact]
    public void Build_SavedMonitorNotConnected_ResolvesToNull()
    {
        var r = MonitorChoices.Build([Vdd], 1920, 1080, "virtual", @"MONITOR\FUS0001\{x}\0007");
        Assert.Null(r.Saved.DeviceName);
    }

    [Fact]
    public void FullDeviceId_SelectsExactlyThatMonitor()
    {
        // Two identical dummies share the model part of the DeviceID; the full one tells them apart.
        var second = Dummy with { DeviceName = @"\\.\DISPLAY8", DeviceId = @"MONITOR\FUS0001\{x}\0008", Left = 6400 };
        Assert.Equal(second.DeviceName, MonitorMatcher.Match(second.DeviceId, [Dummy, second])!.DeviceName);
    }

    [Theory]
    [InlineData("\"virtual\"", "virtual")]
    [InlineData("\"virtual:2\"", "virtual:2")]
    [InlineData("[\"MONITOR\\\\FUS0001\\\\{x}\\\\0007\"]", "MONITOR\\FUS0001\\{x}\\0007")]
    public void Normalize_Monitor_StoresOneEntryList(string json, string expected)
    {
        var field = SettingsCatalog.Find("Slots.Monitors")!;
        var node = SettingsCatalog.Normalize(field, JsonDocument.Parse(json).RootElement)!.AsArray();
        Assert.Equal(expected, (string)node.Single()!);
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"virtualx\"")]
    [InlineData("\"virtual:0\"")]
    [InlineData("5")]
    public void Normalize_Monitor_RejectsBadSpecs(string json)
    {
        var field = SettingsCatalog.Find("Slots.Monitors")!;
        Assert.Throws<ArgumentException>(() => SettingsCatalog.Normalize(field, JsonDocument.Parse(json).RootElement));
    }
}
