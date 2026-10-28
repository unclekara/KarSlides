using KarSlides.Core.Abstractions;
using KarSlides.Core.Model;
using KarSlides.Core.Preflight;

namespace KarSlides.Core.Tests.Fakes;

/// <summary>
/// Simulates PowerPoint navigation over slides with given click counts, including the real quirk:
/// after Previous onto the previous slide the slide is shown in its final state, but ClickIndex reports 0.
/// </summary>
internal sealed class FakePresentationHost(params int[] clicksPerSlide) : IPresentationHost
{
    private int _slide, _click;
    private bool _running;
    private bool _reportZeroClick; // the Previous quirk
    private bool _seeked, _broken; // the automatic-sequence quirk

    public List<string> Calls { get; } = [];
    public ShowPosition Truth => new(_slide, _click);

    /// <summary>
    /// Slides whose timeline starts by itself (e.g. a chain of videos "after previous"). As measured on PowerPoint:
    /// the first Next only seeks that timeline and stays on the slide; Previous from the slide start (after such a
    /// seek or not) stays on the slide too, reports click -1 and leaves the media frozen until the slide is restarted.
    /// </summary>
    public HashSet<int> AutoSequenceSlides { get; } = [];

    /// <summary>The slide is in the frozen state described above.</summary>
    public bool Frozen => _broken;

    /// <summary>The next Previous leaves the current slide frozen (click -1), whatever came before.</summary>
    public bool FreezeOnPrevious { get; set; }
    public MonitorTarget? StartedOn { get; private set; }
    public bool ThrowOnNext { get; set; }

    /// <summary>PowerPoint paused the show (a document window opened); stays until the show is started again.</summary>
    public bool Paused { get; set; }

    public int OtherWindows { get; set; }
    public bool StopShowOnNext { get; set; }
    public TaskCompletionSource? BlockOpen { get; set; }

    public async Task<SlotHandle> OpenAsync(string path, int slotIndex, CancellationToken ct)
    {
        Calls.Add($"Open {Path.GetFileName(path)}");
        OpenedPaths.Add(path);
        if (BlockOpen is not null) await BlockOpen.Task;
        return new SlotHandle(Guid.NewGuid(), path, clicksPerSlide.Length);
    }

    public Task StartShowAsync(SlotHandle handle, MonitorTarget monitor, int startSlide, CancellationToken ct)
    {
        Calls.Add($"Start {startSlide}");
        StartedOn = monitor;
        (_slide, _click, _running, _reportZeroClick) = (startSlide, 0, true, false);
        Paused = false;
        return Task.CompletedTask;
    }

    public Task NextAsync(SlotHandle handle, CancellationToken ct)
    {
        Calls.Add("Next");
        if (ThrowOnNext) throw new TimeoutException("COM call 'View.Next' timed out");
        if (StopShowOnNext) { _running = false; return Task.CompletedTask; }
        _reportZeroClick = false;
        if (AutoSequenceSlides.Contains(_slide) && _click == 0 && !_seeked && !_broken)
        {
            _seeked = true;
            return Task.CompletedTask;
        }
        (_seeked, _broken) = (false, false);
        if (_click < clicksPerSlide[_slide - 1]) _click++;
        else if (_slide < clicksPerSlide.Length) (_slide, _click) = (_slide + 1, 0);
        else _running = false; // PowerPoint ends the show past the last click
        return Task.CompletedTask;
    }

    public Task PreviousAsync(SlotHandle handle, CancellationToken ct)
    {
        Calls.Add("Previous");
        if (_seeked || FreezeOnPrevious || (AutoSequenceSlides.Contains(_slide) && _click == 0))
        {
            (_seeked, _broken, FreezeOnPrevious) = (false, true, false);
            return Task.CompletedTask;
        }
        _broken = false;
        if (_click > 0)
        {
            _click--;
            _reportZeroClick = false;
        }
        else if (_slide > 1)
        {
            _slide--;
            _click = clicksPerSlide[_slide - 1];
            _reportZeroClick = true;
        }
        return Task.CompletedTask;
    }

    public Task GotoSlideAsync(SlotHandle handle, int slide, CancellationToken ct)
    {
        Calls.Add($"GotoSlide {slide}");
        (_slide, _click, _reportZeroClick, _seeked, _broken) = (slide, 0, false, false, false);
        return Task.CompletedTask;
    }

    public Task GotoClickAsync(SlotHandle handle, int slide, int click, CancellationToken ct)
    {
        Calls.Add($"GotoClick {slide} {click}");
        (_slide, _click, _reportZeroClick, _seeked, _broken) = (slide, Math.Min(click, clicksPerSlide[slide - 1]), false, false, false);
        return Task.CompletedTask;
    }

    public Task<HostPosition> GetPositionAsync(SlotHandle handle, CancellationToken ct) =>
        Task.FromResult(new HostPosition(_slide, _broken ? -1 : _reportZeroClick ? 0 : _click, clicksPerSlide[_slide - 1], _running,
            Paused, OtherWindows));

    public Task CloseAsync(SlotHandle handle, CancellationToken ct)
    {
        Calls.Add("Close");
        _running = false;
        return Task.CompletedTask;
    }

    public PresentationInfo Info { get; set; } = new(0, 960, 540, [], [], false, false, false);

    public List<string> OpenedPaths { get; } = [];

    public Task<PresentationInfo> InspectAsync(SlotHandle handle, CancellationToken ct) =>
        Task.FromResult(Info with { SlideCount = clicksPerSlide.Length });

    public Task ExportThumbnailAsync(SlotHandle handle, int slide, string path, int width, int height, CancellationToken ct)
    {
        Calls.Add($"Thumb {slide}");
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47]);
        return Task.CompletedTask;
    }

    /// <summary>Simulates the watchdog killing PowerPoint: the show is gone.</summary>
    public void Crash() => _running = false;
}

internal sealed class FakeOutputSink : IOutputSink
{
    public List<OutputMode> Modes { get; } = [];

    public OutputMode Mode
    {
        get => Modes.Count == 0 ? OutputMode.Live : Modes[^1];
        set => Modes.Add(value);
    }

    public int Frames { get; private set; }

    public void PushVideo(in VideoFrame frame) => Frames++;

    public void Dispose()
    {
    }
}
