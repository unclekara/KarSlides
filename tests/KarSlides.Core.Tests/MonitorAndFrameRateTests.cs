using KarSlides.Core.Configuration;
using KarSlides.Core.Monitors;

namespace KarSlides.Core.Tests;

public class MonitorMatcherTests
{
    private static readonly MonitorDescriptor[] Monitors =
    [
        new(@"\\.\DISPLAY2", @"MONITOR\AUOB69B\{4d36e96e}\0001", "NVIDIA GeForce RTX 3060", false, true, 0, 0, 1920, 1080),
        new(@"\\.\DISPLAY7", @"MONITOR\MTT1337\{4d36e96e}\0003", "Virtual Display Driver", true, false, 3840, 0, 1920, 1080),
        new(@"\\.\DISPLAY5", @"MONITOR\MTT1337\{4d36e96e}\0002", "Virtual Display Driver", true, false, 1920, 0, 1920, 1080),
    ];

    [Theory]
    [InlineData("virtual", @"\\.\DISPLAY5")]      // leftmost virtual
    [InlineData("virtual:1", @"\\.\DISPLAY5")]
    [InlineData("virtual:2", @"\\.\DISPLAY7")]
    [InlineData("VIRTUAL", @"\\.\DISPLAY5")]
    [InlineData(@"\\.\DISPLAY7", @"\\.\DISPLAY7")]
    [InlineData(@"\\.\display2", @"\\.\DISPLAY2")]
    [InlineData("AUOB69B", @"\\.\DISPLAY2")]
    [InlineData("mtt1337", @"\\.\DISPLAY5")]      // ambiguous: leftmost wins, not enumeration order
    [InlineData(@"MTT1337\{4d36e96e}\0003", @"\\.\DISPLAY7")]
    public void Matches(string spec, string expected) =>
        Assert.Equal(expected, MonitorMatcher.Match(spec, Monitors)?.DeviceName);

    [Theory]
    [InlineData("virtual:3")]
    [InlineData("virtual:0")]
    [InlineData("virtual:x")]
    [InlineData("virtualx")]
    [InlineData(@"\\.\DISPLAY9")]
    [InlineData("NOPE")]
    [InlineData("")]
    public void NoMatch(string spec) => Assert.Null(MonitorMatcher.Match(spec, Monitors));
}

public class FrameRateTests
{
    [Theory]
    [InlineData("25", 25, 1)]
    [InlineData("29.97", 30000, 1001)]
    [InlineData("50", 50, 1)]
    [InlineData(" 59.94 ", 60000, 1001)]
    [InlineData("60", 60, 1)]
    public void Parse(string s, int n, int d) => Assert.Equal(new FrameRate(n, d), FrameRate.Parse(s));

    [Fact]
    public void ParseRejectsUnknown() => Assert.Throws<FormatException>(() => FrameRate.Parse("48"));

    [Fact]
    public void Timecode_IsExactFor2997()
    {
        var r = FrameRate.Parse("29.97");
        Assert.Equal(0, r.Timecode(0));
        Assert.Equal(10_010_000, r.Timecode(30)); // 30 frames = 1.001 s
    }
}
