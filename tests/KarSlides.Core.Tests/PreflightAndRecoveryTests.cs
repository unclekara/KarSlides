using KarSlides.Core.Model;
using KarSlides.Core.Preflight;
using KarSlides.Core.Slots;
using KarSlides.Core.Tests.Fakes;
using KarSlides.PowerPoint;
using Microsoft.Extensions.Logging.Abstractions;

namespace KarSlides.Core.Tests;

public class PreflightEvaluatorTests
{
    private static readonly HashSet<string> Installed = new(StringComparer.OrdinalIgnoreCase) { "Arial", "Open Sans" };

    private static PresentationInfo Info(double w = 960, double h = 540, FontUse[]? fonts = null, string[]? linked = null) =>
        new(5, w, h, fonts ?? [], linked ?? [], HasVideo: true, HasAudio: false, HasMorph: true);

    [Fact]
    public void CleanPresentation_HasNoWarnings()
    {
        var r = PreflightEvaluator.Evaluate(Info(fonts: [new("arial", false), new("Unbounded", true)]), Installed, 16.0 / 9, _ => true);

        Assert.Empty(r.Warnings);
        Assert.Equal("16:9", r.AspectRatio);
        Assert.True(r.HasVideo);
        Assert.True(r.HasMorph);
        Assert.Equal(5, r.SlideCount);
    }

    [Fact]
    public void MissingFont_NotEmbedded_IsReported_EmbeddedIsNot()
    {
        var r = PreflightEvaluator.Evaluate(
            Info(fonts: [new("Montserrat", false), new("Montserrat", false), new("Unbounded", true)]), Installed, 16.0 / 9, _ => true);

        Assert.Equal(["Montserrat"], r.MissingFonts);
        Assert.Contains(r.Warnings, w => w.Contains("Montserrat"));
    }

    [Fact]
    public void MissingLinkedFile_IsReported()
    {
        var r = PreflightEvaluator.Evaluate(Info(linked: [@"E:\usb\clip.mp4", @"C:\ok.mp4"]), Installed, 16.0 / 9,
            p => p == @"C:\ok.mp4");

        Assert.Equal([@"E:\usb\clip.mp4"], r.MissingFiles);
    }

    [Fact]
    public void AspectMismatch_IsReported()
    {
        var r = PreflightEvaluator.Evaluate(Info(w: 720, h: 540), Installed, 16.0 / 9, _ => true);

        Assert.Equal("4:3", r.AspectRatio);
        Assert.Contains(r.Warnings, w => w.Contains("4:3"));
    }

    [Fact]
    public void InstalledFonts_ContainsSomethingCommon()
    {
        var fonts = InstalledFonts.Get();
        Assert.Contains("Arial", fonts);
    }
}

public sealed class FileCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"karslides-cache-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Prepare_CopiesFile_AndRemovesMarkOfTheWeb()
    {
        Directory.CreateDirectory(_dir);
        var source = Path.Combine(_dir, "talk.pptx");
        File.WriteAllText(source, "v1");
        File.WriteAllText(source + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        var cache = new FileCache(Path.Combine(_dir, "cache"));

        var cached = cache.Prepare(source);

        Assert.NotEqual(source, cached);
        Assert.Equal("v1", File.ReadAllText(cached));
        Assert.False(File.Exists(cached + ":Zone.Identifier"));
    }

    [Fact]
    public void SameSource_SameFolder_AndPrepareOverwrites()
    {
        Directory.CreateDirectory(_dir);
        var source = Path.Combine(_dir, "talk.pptx");
        var cache = new FileCache(Path.Combine(_dir, "cache"));
        File.WriteAllText(source, "v1");
        var a = cache.Prepare(source);
        File.WriteAllText(source, "v2");
        var b = cache.Prepare(source);

        Assert.Equal(a, b);
        Assert.Equal("v2", File.ReadAllText(b));
        Assert.NotEqual(cache.FolderFor(source), cache.FolderFor(Path.Combine(_dir, "other.pptx")));
    }
}

public sealed class SlotM2Tests : IDisposable
{
    private static readonly MonitorTarget Monitor = new(@"\\.\DISPLAY5", 1920, 0, 1920, 1080);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"karslides-slot-{Guid.NewGuid():N}");
    private readonly string _file;

    public SlotM2Tests()
    {
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "talk.pptx");
        File.WriteAllText(_file, "v1");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private SlotController Create(FakePresentationHost host, FakeOutputSink? output = null, bool thumbnails = false) =>
        new(1, host, output ?? new FakeOutputSink(), () => Monitor, new SlotOptions
        {
            HoldRelease = TimeSpan.Zero,
            Cache = new FileCache(Path.Combine(_dir, "cache")),
            Evaluate = info => PreflightEvaluator.Evaluate(info, new HashSet<string> { "Arial" }, 16.0 / 9, File.Exists),
            Thumbnails = thumbnails,
        }, NullLogger.Instance);

    [Fact]
    public async Task Load_OpensCachedCopy_AndReportsPreflight()
    {
        var host = new FakePresentationHost(0, 0) { Info = new(0, 720, 540, [new("Montserrat", false)], [], true, false, false) };
        var slot = Create(host);

        var s = await slot.LoadAsync(_file, 1, default);

        Assert.StartsWith(Path.Combine(_dir, "cache"), host.OpenedPaths.Single());
        Assert.Equal(_file, s.File); // the API shows the source
        Assert.NotNull(s.Preflight);
        Assert.Equal(["Montserrat"], s.Preflight!.MissingFonts);
        Assert.Equal(2, s.Preflight.Warnings.Count); // font + 4:3
    }

    [Fact]
    public async Task Thumbnails_AreExportedDuringLoad_BeforeTheShowStarts()
    {
        var host = new FakePresentationHost(0, 1, 0);
        var slot = Create(host, thumbnails: true);

        await slot.LoadAsync(_file, 1, default);

        Assert.Equal(3, slot.Status.Thumbnails);
        Assert.NotNull(slot.ThumbnailPath(3));
        Assert.Null(slot.ThumbnailPath(4));
        Assert.True(host.Calls.IndexOf("Thumb 3") < host.Calls.IndexOf("Start 1"));
    }

    [Fact]
    public async Task Recovery_ReusesThumbnails_ReloadRegenerates()
    {
        var host = new FakePresentationHost(0, 0);
        var slot = Create(host, thumbnails: true);
        await slot.LoadAsync(_file, 1, default);
        host.Calls.Clear();

        host.Crash();
        slot.BeginRecovery("test");
        await slot.RecoverAsync(default);
        Assert.DoesNotContain(host.Calls, c => c.StartsWith("Thumb"));
        Assert.Equal(2, slot.Status.Thumbnails);

        await slot.ReloadAsync(default);
        Assert.Equal(2, host.Calls.Count(c => c.StartsWith("Thumb")));
    }

    [Fact]
    public async Task Reload_RecopiesSource_AndReturnsToSameSlideAndClick()
    {
        var host = new FakePresentationHost(0, 3, 0);
        var slot = Create(host);
        await slot.LoadAsync(_file, 1, default);
        await slot.GotoAsync(2, 2, default);
        File.WriteAllText(_file, "v2");

        var s = await slot.ReloadAsync(default);

        Assert.Equal((2, 2), (s.Slide, s.Click));
        Assert.Equal(new ShowPosition(2, 2), host.Truth);
        Assert.Equal("v2", File.ReadAllText(host.OpenedPaths[^1]));
        Assert.Equal(SlotState.Ready, s.State);
    }

    [Fact]
    public async Task Recovery_FreezesOutput_ThenRestoresTrackedPosition()
    {
        var host = new FakePresentationHost(0, 2, 1);
        var output = new FakeOutputSink();
        var slot = Create(host, output);
        await slot.LoadAsync(_file, 3, default);
        await slot.PreviousAsync(default); // 2.2 – PowerPoint would report click 0 here

        host.Crash();
        slot.BeginRecovery("POWERPNT.EXE exited");
        Assert.Equal(SlotState.Recovering, slot.Status.State);
        Assert.Equal(OutputMode.Freeze, output.Mode);

        var s = await slot.RecoverAsync(default);

        Assert.Equal(SlotState.Ready, s.State);
        Assert.Equal((2, 2), (s.Slide, s.Click));
        Assert.Equal(new ShowPosition(2, 2), host.Truth);
        Assert.Equal(OutputMode.Live, output.Mode);
        Assert.DoesNotContain(OutputMode.Black, output.Modes); // never black during recovery
        Assert.Equal(host.OpenedPaths[0], host.OpenedPaths[^1]); // reopened from the cache, no re-copy
    }

    [Fact]
    public async Task Recovery_OfEmptySlot_DoesNothing()
    {
        var host = new FakePresentationHost(0);
        var slot = Create(host);
        slot.BeginRecovery("test");
        var s = await slot.RecoverAsync(default);
        Assert.Equal(SlotState.Empty, s.State);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task OnProgram_LoadUnloadReload_NeedForce_NavigationDoesNot()
    {
        var host = new FakePresentationHost(0, 0);
        var slot = Create(host);
        await slot.LoadAsync(_file, 1, default);
        slot.SetTally(new Tally(OnProgram: true, OnPreview: false));

        await Assert.ThrowsAsync<OnAirException>(() => slot.LoadAsync(_file, 1, default));
        await Assert.ThrowsAsync<OnAirException>(() => slot.ReloadAsync(default));
        await Assert.ThrowsAsync<OnAirException>(() => slot.UnloadAsync(default));
        Assert.Equal(2, (await slot.NextAsync(default)).Slide);

        Assert.Equal(SlotState.Ready, (await slot.ReloadAsync(default, force: true)).State);
        Assert.Equal(SlotState.Empty, (await slot.UnloadAsync(default, force: true)).State);
    }

    [Fact]
    public async Task Changed_IsRaisedWithTallyAndPosition()
    {
        var host = new FakePresentationHost(0, 0);
        var slot = Create(host);
        var events = new List<SlotStatus>();
        slot.Changed += events.Add;

        await slot.LoadAsync(_file, 1, default);
        await slot.NextAsync(default);
        slot.SetTally(new Tally(true, false));

        Assert.Contains(events, e => e.State == SlotState.Ready);
        Assert.Contains(events, e => e.Slide == 2);
        Assert.True(events[^1].OnProgram);
    }

    [Fact]
    public async Task Ping_ReportsShowEnded()
    {
        var host = new FakePresentationHost(0, 0);
        var slot = Create(host);
        await slot.LoadAsync(_file, 1, default);
        Assert.True((await slot.PingAsync(default)).Running);
        host.Crash();
        Assert.False((await slot.PingAsync(default)).Running);
    }
}

