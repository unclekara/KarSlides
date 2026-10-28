using System.Net.Http.Json;
using System.Text.Json;
using KarSlides.Control;
using KarSlides.Core.Clicker;
using KarSlides.Core.Configuration;
using KarSlides.Core.Model;
using KarSlides.Core.Slots;
using KarSlides.Core.Tests.Fakes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging.Abstractions;

namespace KarSlides.Core.Tests;

public class ClickerKeymapTests
{
    private const int PageDown = 0x22, PageUp = 0x21, Right = 0x27, Space = 0x20, B = 0x42, A = 0x41;

    // A key set wider than the default: exercises actions on unswallowed keys.
    private static readonly ClickerOptions Spec = new()
    {
        Next = ["PageDown", "Right", "Space"],
        Prev = ["PageUp", "Left"],
        Black = ["B", "OemPeriod"],
        Swallow = ["PageDown", "PageUp"],
    };

    private static ClickerKeymap Map() => new(Spec);

    [Fact]
    public void DefaultKeys_AreOnlyPageUpPageDown()
    {
        var m = new ClickerKeymap(new ClickerOptions());
        Assert.Equal(new ClickerDecision(ClickerAction.Next, true), m.Decide(PageDown, true, false));
        Assert.Equal(new ClickerDecision(ClickerAction.Previous, true), m.Decide(PageUp, true, false));
        Assert.Equal(ClickerDecision.PassThrough, m.Decide(Right, true, false));
        Assert.Equal(ClickerDecision.PassThrough, m.Decide(Space, true, false)); // typing in vMix is safe
        Assert.Equal(ClickerDecision.PassThrough, m.Decide(B, true, false));
    }

    [Theory]
    [InlineData("PageDown", 0x22)]
    [InlineData("pageup", 0x21)]
    [InlineData("Space", 0x20)]
    [InlineData("B", 0x42)]
    [InlineData("OemPeriod", 0xBE)]
    [InlineData("D5", 0x35)]
    [InlineData("F5", 0x74)]
    public void ParsesKeyNames(string name, int vk) => Assert.Equal(vk, VirtualKeys.Parse(name));

    [Fact]
    public void UnknownKeyName_Throws() => Assert.Throws<FormatException>(() => VirtualKeys.Parse("Banana"));

    [Fact]
    public void DefaultMap_ActionsAndSwallow()
    {
        var m = Map();
        Assert.Equal(new ClickerDecision(ClickerAction.Next, true), m.Decide(PageDown, true, false));
        Assert.Equal(new ClickerDecision(ClickerAction.Next, false), m.Decide(Right, true, false));
        Assert.Equal(new ClickerDecision(ClickerAction.Next, false), m.Decide(Space, true, false));
        Assert.Equal(new ClickerDecision(ClickerAction.Previous, true), m.Decide(PageUp, true, false));
        Assert.Equal(new ClickerDecision(ClickerAction.ToggleBlack, false), m.Decide(B, true, false));
        Assert.Equal(ClickerDecision.PassThrough, m.Decide(A, true, false));
    }

    [Fact]
    public void HeldKey_ActsOnce_SwallowedKeyStaysSwallowedUntilUp()
    {
        var m = Map();
        Assert.Equal(ClickerAction.Next, m.Decide(PageDown, true, false).Action);
        Assert.Equal(new ClickerDecision(null, true), m.Decide(PageDown, true, false)); // auto-repeat
        Assert.Equal(new ClickerDecision(null, true), m.Decide(PageDown, false, false)); // up
        Assert.Equal(ClickerAction.Next, m.Decide(PageDown, true, false).Action); // next press
    }

    [Fact]
    public void WithModifiers_KeyPassesThroughUntouched()
    {
        var m = Map();
        Assert.Equal(ClickerDecision.PassThrough, m.Decide(B, true, modifiers: true)); // Ctrl+B in vMix
        Assert.Equal(ClickerDecision.PassThrough, m.Decide(PageDown, true, modifiers: true));
        Assert.Equal(ClickerDecision.PassThrough, m.Decide(PageDown, false, modifiers: false)); // its key-up
    }
}

public sealed class ClickerControllerTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"karslides-clk-{Guid.NewGuid():N}.pptx");
    private readonly FakePresentationHost _host = new(0, 0, 0);
    private readonly SlotController _slot;
    private readonly ClickerController _clicker;

    public ClickerControllerTests()
    {
        File.WriteAllText(_file, "x");
        _slot = new SlotController(1, _host, new FakeOutputSink(), () => new MonitorTarget(@"\\.\DISPLAY5", 1920, 0, 1920, 1080),
            new SlotOptions { HoldRelease = TimeSpan.Zero }, NullLogger.Instance);
        _clicker = new ClickerController(new ClickerOptions { Black = ["B"] }, new SlotSet([_slot]), NullLogger.Instance);
    }

    public void Dispose() => File.Delete(_file);

    private void Press(int vk)
    {
        _clicker.Handle(vk, true, false);
        _clicker.Handle(vk, false, false);
    }

    [Fact]
    public async Task Presses_DriveTheSlot_InOrder()
    {
        await _slot.LoadAsync(_file, 1, default);
        Press(0x22);
        Press(0x22);
        Press(0x21);
        await _clicker.Drain();
        Assert.Equal(2, _slot.Status.Slide);
    }

    [Fact]
    public async Task B_TogglesBlack()
    {
        await _slot.LoadAsync(_file, 1, default);
        Press(0x42);
        await _clicker.Drain();
        Assert.True(_slot.Status.Black);
        Press(0x42);
        await _clicker.Drain();
        Assert.False(_slot.Status.Black);
    }

    [Fact]
    public async Task Disabled_DoesNothing_AndSwallowsNothing()
    {
        await _slot.LoadAsync(_file, 1, default);
        _clicker.Enabled = false;
        Assert.False(_clicker.Handle(0x22, true, false));
        await _clicker.Drain();
        Assert.Equal(1, _slot.Status.Slide);
    }

    [Fact]
    public async Task PressOnEmptySlot_IsHarmless()
    {
        Press(0x22);
        await _clicker.Drain(); // logged, not thrown
        Assert.Equal(SlotState.Empty, _slot.Status.State);
    }

    [Fact]
    public async Task Api_TogglesClicker()
    {
        await using var app = ControlServer.Create(new ControlOptions(), new SlotSet([_slot]), new AppStatus(),
            b => b.WebHost.UseTestServer(), clicker: _clicker);
        await app.StartAsync();
        var http = app.GetTestClient();

        var j = JsonDocument.Parse(await http.GetStringAsync("/api/clicker")).RootElement;
        Assert.True(j.GetProperty("enabled").GetBoolean());
        Assert.Equal("PageDown", j.GetProperty("next")[0].GetString());

        await http.PostAsJsonAsync("/api/clicker", new { enabled = false });
        Assert.False(_clicker.Enabled);
    }

    [Fact]
    public async Task Api_ListsInboxPresentations()
    {
        var inbox = Path.Combine(Path.GetTempPath(), $"karslides-inbox-{Guid.NewGuid():N}");
        Directory.CreateDirectory(inbox);
        File.WriteAllText(Path.Combine(inbox, "b talk.pptx"), "x");
        File.WriteAllText(Path.Combine(inbox, "a talk.pptx"), "x");
        File.WriteAllText(Path.Combine(inbox, "~$a talk.pptx"), "lock");
        File.WriteAllText(Path.Combine(inbox, "notes.txt"), "x");
        try
        {
            await using var app = ControlServer.Create(new ControlOptions(), new SlotSet([_slot]), new AppStatus(),
                b => b.WebHost.UseTestServer(), new ControlContext(inbox, inbox));
            await app.StartAsync();
            var files = JsonDocument.Parse(await app.GetTestClient().GetStringAsync("/api/files")).RootElement;
            Assert.Equal(["a talk.pptx", "b talk.pptx"], files.EnumerateArray().Select(f => f.GetProperty("name").GetString()));
        }
        finally
        {
            Directory.Delete(inbox, recursive: true);
        }
    }
}
