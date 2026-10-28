using KarSlides.Core.Model;
using KarSlides.Core.Slots;
using KarSlides.Core.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace KarSlides.Core.Tests;

public sealed class SlotControllerTests : IDisposable
{
    private static readonly MonitorTarget Monitor = new(@"\\.\DISPLAY5", 1920, 0, 1920, 1080);
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"karslides-test-{Guid.NewGuid():N}.pptx");

    public SlotControllerTests() => File.WriteAllText(_file, "fake");

    public void Dispose() => File.Delete(_file);

    private static SlotController Create(FakePresentationHost host, FakeOutputSink output) =>
        new(1, host, output, () => Monitor, new SlotOptions { HoldRelease = TimeSpan.Zero }, NullLogger.Instance);

    [Fact]
    public async Task Load_StartsShowOnResolvedMonitor_AndGoesLive()
    {
        var host = new FakePresentationHost(0, 2, 1);
        var output = new FakeOutputSink();
        var slot = Create(host, output);

        var s = await slot.LoadAsync(_file, 1, default);

        Assert.Equal(SlotState.Ready, s.State);
        Assert.Equal(3, s.SlideCount);
        Assert.Equal((1, 0), (s.Slide, s.Click));
        Assert.Equal(Monitor, host.StartedOn);
        Assert.Equal(OutputMode.Live, output.Mode);
        // Frozen while loading, live only when ready: the desktop never goes on air.
        Assert.Equal([OutputMode.Freeze, OutputMode.Live], output.Modes);
    }

    [Fact]
    public async Task Load_MissingFile_ThrowsAndStaysEmpty()
    {
        var slot = Create(new FakePresentationHost(0), new FakeOutputSink());
        await Assert.ThrowsAsync<FileNotFoundException>(() => slot.LoadAsync(_file + ".missing", 1, default));
        Assert.Equal(SlotState.Empty, slot.Status.State);
    }

    [Fact]
    public async Task Load_StartSlideOutOfRange_StartsFromFirst()
    {
        var host = new FakePresentationHost(0, 0);
        var slot = Create(host, new FakeOutputSink());
        var s = await slot.LoadAsync(_file, 7, default);
        Assert.Equal(1, s.Slide);
        Assert.Contains("Start 1", host.Calls);
    }

    [Fact]
    public async Task Next_StepsThroughClicksThenSlides()
    {
        var host = new FakePresentationHost(0, 2, 0);
        var slot = Create(host, new FakeOutputSink());
        await slot.LoadAsync(_file, 1, default);

        var positions = new List<(int, int)>();
        for (var i = 0; i < 4; i++)
        {
            var s = await slot.NextAsync(default);
            positions.Add((s.Slide, s.Click));
        }

        Assert.Equal([(2, 0), (2, 1), (2, 2), (3, 0)], positions);
    }

    [Fact]
    public async Task Previous_OntoPreviousSlide_TracksFinalState_DespiteHostReportingClickZero()
    {
        var host = new FakePresentationHost(0, 2, 0);
        var slot = Create(host, new FakeOutputSink());
        await slot.LoadAsync(_file, 3, default);

        var s = await slot.PreviousAsync(default);

        Assert.Equal(new ShowPosition(2, 2), host.Truth);
        Assert.Equal((2, 2), (s.Slide, s.Click)); // not (2, 0) as PowerPoint reports
        Assert.Equal(2, s.ClickCount);
    }

    [Fact]
    public async Task Previous_WithinSlide_StepsBackOneClick()
    {
        var host = new FakePresentationHost(0, 2, 0);
        var slot = Create(host, new FakeOutputSink());
        await slot.LoadAsync(_file, 3, default);
        await slot.PreviousAsync(default); // 2.2

        var s = await slot.PreviousAsync(default);

        Assert.Equal((2, 1), (s.Slide, s.Click));
        Assert.Equal(host.Truth, new ShowPosition(s.Slide, s.Click));
    }

    [Fact]
    public async Task Next_OnAutoSequenceSlide_FirstPressOnlySeeks_SecondGoesOn()
    {
        var host = new FakePresentationHost(0, 0, 0);
        host.AutoSequenceSlides.Add(2);
        var slot = Create(host, new FakeOutputSink());
        await slot.LoadAsync(_file, 2, default);

        var first = await slot.NextAsync(default);
        var second = await slot.NextAsync(default);

        Assert.Equal((2, 0), (first.Slide, first.Click));
        Assert.Equal((3, 0), (second.Slide, second.Click));
    }

    [Fact]
    public async Task Previous_AfterSeekOnAutoSequenceSlide_GoesToPreviousSlideFinalState_WithoutViewPrevious()
    {
        var host = new FakePresentationHost(2, 0, 0);
        host.AutoSequenceSlides.Add(2);
        var slot = Create(host, new FakeOutputSink());
        await slot.LoadAsync(_file, 2, default);
        await slot.NextAsync(default); // seek only, still 2.0

        var s = await slot.PreviousAsync(default);

        Assert.False(host.Frozen);
        Assert.DoesNotContain("Previous", host.Calls);
        Assert.Equal((1, 2), (s.Slide, s.Click));
        Assert.Equal(host.Truth, new ShowPosition(s.Slide, s.Click));
    }

    [Fact]
    public async Task Previous_FromStartOfAutoStartSlide_GoesToPreviousSlide_NotStuck()
    {
        // Console+StandAlone.pptx slide 7: effects "with previous" at the start, 2 clicks. Previous from 7.0 used to
        // restart slide 7 every time: the operator could not get back to slide 6.
        var host = new FakePresentationHost(0, 1, 2);
        host.AutoSequenceSlides.Add(3);
        var slot = Create(host, new FakeOutputSink());
        await slot.LoadAsync(_file, 3, default);

        var s = await slot.PreviousAsync(default);

        Assert.False(host.Frozen);
        Assert.Equal((2, 1), (s.Slide, s.Click)); // previous slide, final state
        Assert.Equal(host.Truth, new ShowPosition(s.Slide, s.Click));
        s = await slot.PreviousAsync(default); // and further back as usual
        Assert.Equal((2, 0), (s.Slide, s.Click));
    }

    [Fact]
    public async Task Previous_LeavingSlideFrozenMidSlide_RestartsTheSlide()
    {
        var host = new FakePresentationHost(0, 2, 0);
        var slot = Create(host, new FakeOutputSink());
        await slot.LoadAsync(_file, 2, default);
        await slot.NextAsync(default); // 2.1
        host.FreezeOnPrevious = true;

        var s = await slot.PreviousAsync(default);

        Assert.False(host.Frozen);
        Assert.Equal("GotoSlide 2", host.Calls[^1]);
        Assert.Equal((2, 0), (s.Slide, s.Click));
    }

    [Fact]
    public async Task TrackedPosition_MatchesTruth_OverRandomWalk()
    {
        var host = new FakePresentationHost(0, 3, 1, 0, 2, 1);
        host.AutoSequenceSlides.Add(4);
        var slot = Create(host, new FakeOutputSink());
        await slot.LoadAsync(_file, 1, default);
        var rnd = new Random(42);

        for (var i = 0; i < 300; i++)
        {
            var s = rnd.Next(10) switch
            {
                < 5 => await slot.NextAsync(default),
                < 9 => await slot.PreviousAsync(default),
                _ => await slot.GotoAsync(rnd.Next(1, 7), rnd.Next(0, 4), default),
            };
            Assert.Equal(host.Truth, new ShowPosition(s.Slide, s.Click));
            Assert.False(host.Frozen);
        }
    }

    [Fact]
    public async Task Next_AtEnd_IsIgnored_SoTheShowNeverEnds()
    {
        var host = new FakePresentationHost(0, 1);
        var slot = Create(host, new FakeOutputSink());
        await slot.LoadAsync(_file, 2, default);
        await slot.NextAsync(default); // 2.1 — last click

        var s = await slot.NextAsync(default);

        Assert.Equal(SlotState.Ready, s.State);
        Assert.Equal((2, 1), (s.Slide, s.Click));
        Assert.Equal(1, host.Calls.Count(c => c == "Next"));
    }

    [Fact]
    public async Task Previous_AtStart_IsIgnored()
    {
        var host = new FakePresentationHost(0, 1);
        var slot = Create(host, new FakeOutputSink());
        await slot.LoadAsync(_file, 1, default);

        await slot.PreviousAsync(default);

        Assert.DoesNotContain("Previous", host.Calls);
    }

    [Fact]
    public async Task Goto_WithClick_RestoresExactPosition()
    {
        var host = new FakePresentationHost(0, 3, 0);
        var slot = Create(host, new FakeOutputSink());
        await slot.LoadAsync(_file, 1, default);

        var s = await slot.GotoAsync(2, 2, default);

        Assert.Equal((2, 2), (s.Slide, s.Click));
        Assert.Contains("GotoClick 2 2", host.Calls);
    }

    [Fact]
    public async Task Goto_OutOfRange_ThrowsAndKeepsState()
    {
        var slot = Create(new FakePresentationHost(0, 0), new FakeOutputSink());
        await slot.LoadAsync(_file, 1, default);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => slot.GotoAsync(9, 0, default));
        Assert.Equal(SlotState.Ready, slot.Status.State);
    }

    [Fact]
    public async Task FirstAndLast()
    {
        var host = new FakePresentationHost(1, 0, 2);
        var slot = Create(host, new FakeOutputSink());
        await slot.LoadAsync(_file, 2, default);

        Assert.Equal(3, (await slot.LastAsync(default)).Slide);
        Assert.Equal(1, (await slot.FirstAsync(default)).Slide);
    }

    [Fact]
    public async Task Navigation_WhenEmpty_ThrowsSlotStateException()
    {
        var slot = Create(new FakePresentationHost(0), new FakeOutputSink());
        await Assert.ThrowsAsync<SlotStateException>(() => slot.NextAsync(default));
    }

    [Fact]
    public async Task HostTimeout_PutsSlotInError_AndFreezesOutput()
    {
        var host = new FakePresentationHost(0, 0);
        var output = new FakeOutputSink();
        var slot = Create(host, output);
        await slot.LoadAsync(_file, 1, default);
        host.ThrowOnNext = true;

        await Assert.ThrowsAsync<TimeoutException>(() => slot.NextAsync(default));

        Assert.Equal(SlotState.Error, slot.Status.State);
        Assert.Contains("timed out", slot.Status.Error);
        Assert.Equal(OutputMode.Freeze, output.Mode); // last valid frame stays on air
        await Assert.ThrowsAsync<SlotStateException>(() => slot.NextAsync(default));
    }

    [Fact]
    public async Task ShowEndedUnexpectedly_PutsSlotInError()
    {
        var host = new FakePresentationHost(0, 0);
        var slot = Create(host, new FakeOutputSink());
        await slot.LoadAsync(_file, 1, default);
        host.StopShowOnNext = true;

        await Assert.ThrowsAsync<SlotStateException>(() => slot.NextAsync(default));
        Assert.Equal(SlotState.Error, slot.Status.State);
    }

    [Fact]
    public async Task Error_CanBeClearedByUnload_ThenLoadAgain()
    {
        var host = new FakePresentationHost(0, 0);
        var slot = Create(host, new FakeOutputSink());
        await slot.LoadAsync(_file, 1, default);
        host.ThrowOnNext = true;
        await Assert.ThrowsAsync<TimeoutException>(() => slot.NextAsync(default));

        Assert.Equal(SlotState.Empty, (await slot.UnloadAsync(default)).State);
        host.ThrowOnNext = false;
        Assert.Equal(SlotState.Ready, (await slot.LoadAsync(_file, 1, default)).State);
    }

    [Fact]
    public async Task Unload_ClosesShow_AndKeepsLastFrame()
    {
        var host = new FakePresentationHost(0, 0);
        var output = new FakeOutputSink();
        var slot = Create(host, output);
        await slot.LoadAsync(_file, 1, default);

        var s = await slot.UnloadAsync(default);

        Assert.Equal(SlotState.Empty, s.State);
        Assert.Null(s.File);
        Assert.Contains("Close", host.Calls);
        Assert.Equal(OutputMode.Freeze, output.Mode); // never the desktop, never black
    }

    [Fact]
    public async Task LoadOverLoaded_UnloadsPreviousFirst()
    {
        var host = new FakePresentationHost(0, 0);
        var slot = Create(host, new FakeOutputSink());
        await slot.LoadAsync(_file, 1, default);

        await slot.LoadAsync(_file, 2, default);

        Assert.Equal(["Open", "Start 1", "Close", "Open", "Start 2"],
            host.Calls.Select(c => c.StartsWith("Open") ? "Open" : c).ToArray());
    }

    [Fact]
    public async Task BlackAndFreeze_AreOutputModes_BlackWins()
    {
        var output = new FakeOutputSink();
        var slot = Create(new FakePresentationHost(0), output);
        await slot.LoadAsync(_file, 1, default);

        slot.SetFreeze(true);
        Assert.Equal(OutputMode.Freeze, output.Mode);
        slot.SetBlack(true);
        Assert.Equal(OutputMode.Black, output.Mode);
        slot.SetBlack(false);
        Assert.Equal(OutputMode.Freeze, output.Mode);
        slot.SetFreeze(false);
        Assert.Equal(OutputMode.Live, output.Mode);
    }

    [Fact]
    public async Task OperatorFreeze_SurvivesReload()
    {
        var output = new FakeOutputSink();
        var slot = Create(new FakePresentationHost(0), output);
        slot.SetFreeze(true);

        await slot.LoadAsync(_file, 1, default);

        Assert.Equal(OutputMode.Freeze, output.Mode);
        Assert.True(slot.Status.Freeze);
    }

    [Fact]
    public async Task Commands_AreSerialised()
    {
        var host = new FakePresentationHost(0, 0) { BlockOpen = new TaskCompletionSource() };
        var slot = Create(host, new FakeOutputSink());

        var load = slot.LoadAsync(_file, 1, default);
        var next = slot.NextAsync(default);
        Assert.False(next.IsCompleted);

        host.BlockOpen.SetResult();
        await load;
        Assert.Equal(2, (await next).Slide);
    }
}
