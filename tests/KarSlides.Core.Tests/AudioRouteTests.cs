using KarSlides.Control;
using KarSlides.Core.Audio;
using Microsoft.Extensions.Logging.Abstractions;

namespace KarSlides.Core.Tests;

public class AudioRouteTests
{
    private const string Realtek = "{0.0.0.00000000}.{realtek}";
    private const string Monitor = "{0.0.0.00000000}.{monitor}";

    private sealed class FakeRouter : IAudioRouter
    {
        public List<PlaybackDevice> List { get; } =
        [
            new(Realtek, "Speakers (Realtek)", IsDefault: true, EnhancementsEnabled: false),
            new(Monitor, "Monitor (HDMI)", IsDefault: false, EnhancementsEnabled: true),
        ];

        public Dictionary<uint, string> Routes { get; } = new();
        public List<string> Calls { get; } = [];
        public bool Broken { get; set; }
        public int RouteFailures { get; set; }  // Route throws this many times first
        public bool NotAnswering { get; set; }  // Routed throws: a just-started process (E_INVALIDARG)

        public IReadOnlyList<PlaybackDevice> Devices() => List.ToList();

        public void Route(uint pid, string? deviceId)
        {
            if (Broken) throw new InvalidOperationException("AudioPolicyConfig is not available");
            if (RouteFailures > 0 && RouteFailures-- > 0) throw new InvalidOperationException("not yet");
            Calls.Add($"{pid}->{deviceId ?? "default"}");
            if (deviceId is null) Routes.Remove(pid);
            else Routes[pid] = deviceId;
        }

        public string Routed(uint pid) =>
            NotAnswering ? throw new ArgumentException("E_INVALIDARG") : Routes.GetValueOrDefault(pid, "");
    }

    private static AudioRouteKeeper Keeper(FakeRouter router, string device) => new(router, device, NullLogger.Instance);

    [Fact]
    public void NewProcess_IsRouted_AtOnce()
    {
        var router = new FakeRouter();
        var keeper = Keeper(router, Monitor);

        Assert.True(keeper.Tick(100));

        Assert.Equal(["100->" + Monitor], router.Calls);
        Assert.Equal("Monitor (HDMI)", keeper.State.Effective?.Name);
        Assert.True(keeper.State.Effective!.EnhancementsEnabled); // the enhancements check follows the routed device
        Assert.Null(keeper.State.Warning);
    }

    [Fact]
    public void WatchdogRestart_RoutesTheNewProcess()
    {
        var router = new FakeRouter();
        var keeper = Keeper(router, Monitor);
        keeper.Tick(100);

        keeper.Tick(null); // PowerPoint killed
        keeper.Tick(200);  // restarted

        Assert.Equal(["100->" + Monitor, "200->" + Monitor], router.Calls);
    }

    [Fact]
    public void SameProcess_IsCheckedPeriodically_AndRoutedBackIfChangedByHand()
    {
        var router = new FakeRouter();
        var keeper = Keeper(router, Monitor);
        keeper.Tick(100);

        for (var i = 1; i < AudioRouteKeeper.CheckEveryTicks; i++) Assert.False(keeper.Tick(100));
        Assert.Single(router.Calls);

        router.Routes.Remove(100); // someone set PowerPoint back to "default" in the Windows volume mixer
        Assert.True(keeper.Tick(100));
        Assert.Equal(2, router.Calls.Count);
    }

    [Fact]
    public void MissingDevice_Warns_AndRoutesWhenItComesBack()
    {
        var router = new FakeRouter();
        var monitor = router.List[1];
        router.List.RemoveAt(1);
        var keeper = Keeper(router, Monitor);

        keeper.Tick(100);
        Assert.Empty(router.Calls);
        Assert.Contains("not connected", keeper.State.Warning);
        Assert.Equal("Speakers (Realtek)", keeper.State.Effective?.Name); // Windows plays to the default meanwhile

        router.List.Add(monitor);
        for (var i = 0; i < AudioRouteKeeper.CheckEveryTicks; i++) keeper.Tick(100);

        Assert.Equal(["100->" + Monitor], router.Calls);
        Assert.Null(keeper.State.Warning);
    }

    [Fact]
    public void Default_ResetsAChoiceLeftOverByACrash()
    {
        var router = new FakeRouter();
        router.Routes[100] = Monitor;
        var keeper = Keeper(router, "");

        keeper.Tick(100);

        Assert.Equal(["100->default"], router.Calls);
        Assert.Equal("Speakers (Realtek)", keeper.State.Effective?.Name);
    }

    [Fact]
    public void Default_DoesNothing_WhenNothingIsLeftOver()
    {
        var router = new FakeRouter();
        var keeper = Keeper(router, "");

        keeper.Tick(100);
        keeper.Release();

        Assert.Empty(router.Calls);
    }

    [Fact]
    public void Release_ResetsToDefault_OnlyWhatWasRouted()
    {
        var router = new FakeRouter();
        var keeper = Keeper(router, Monitor);
        keeper.Tick(100);

        keeper.Release();
        keeper.Release();

        Assert.Equal(["100->" + Monitor, "100->default"], router.Calls);
        Assert.Empty(router.Routes);
    }

    [Fact]
    public void BrokenApi_Warns_AndDoesNotThrow()
    {
        var router = new FakeRouter { Broken = true };
        var keeper = Keeper(router, Monitor);

        keeper.Tick(100);
        Assert.Null(keeper.State.Warning); // one failure is not reported yet
        for (var i = 1; i < AudioRouteKeeper.FailuresBeforeWarning; i++) Assert.True(keeper.Tick(100)); // retried every tick
        keeper.Release();

        Assert.Contains("Cannot route", keeper.State.Warning);
        Assert.Equal("Speakers (Realtek)", keeper.State.Effective?.Name);
    }

    [Fact]
    public void NoPowerPoint_ReportsTheConfiguredDevice()
    {
        var router = new FakeRouter();
        var keeper = Keeper(router, Monitor);

        Assert.True(keeper.Tick(null));

        Assert.Equal("Monitor (HDMI)", keeper.State.Effective?.Name);
        Assert.Empty(router.Calls);
    }

    [Fact]
    public void Choices_ResolveCurrentAndSaved()
    {
        var devices = new FakeRouter().Devices();

        var r = AudioDeviceChoices.Build(devices, "", Monitor);
        Assert.Equal("Speakers (Realtek)", r.Current.Name); // "" = Windows default
        Assert.Equal("Monitor (HDMI)", r.Saved.Name);

        Assert.Null(AudioDeviceChoices.Build(devices, "{0.0.0.00000000}.{gone}", "").Current.Name);
    }

    [Fact]
    public void JustStartedProcess_NotAnsweringTheQuery_IsRoutedAnyway()
    {
        var router = new FakeRouter { NotAnswering = true };
        var keeper = Keeper(router, Monitor);

        keeper.Tick(100);

        Assert.Equal(["100->" + Monitor], router.Calls);
        Assert.Null(keeper.State.Warning);
    }

    [Fact]
    public void BriefRouteFailures_AreRetriedNextTick_WithoutWarning()
    {
        var router = new FakeRouter { RouteFailures = 2 };
        var keeper = Keeper(router, Monitor);

        Assert.True(keeper.Tick(100));
        Assert.True(keeper.Tick(100));
        Assert.True(keeper.Tick(100));

        Assert.Equal(["100->" + Monitor], router.Calls);
        Assert.Null(keeper.State.Warning);
        Assert.Equal("Monitor (HDMI)", keeper.State.Effective?.Name);
    }

    [Fact]
    public void Default_LeftoverCheck_WaitsUntilTheProcessAnswers()
    {
        var router = new FakeRouter { NotAnswering = true };
        router.Routes[100] = Monitor;
        var keeper = Keeper(router, "");

        keeper.Tick(100);
        Assert.Empty(router.Calls);

        router.NotAnswering = false;
        for (var i = 0; i < AudioRouteKeeper.CheckEveryTicks; i++) keeper.Tick(100);
        Assert.Equal(["100->default"], router.Calls);

        router.Routes[100] = Monitor; // chosen by hand in the volume mixer afterwards: left alone
        for (var i = 0; i < AudioRouteKeeper.CheckEveryTicks; i++) keeper.Tick(100);
        Assert.Single(router.Calls);
    }
}
