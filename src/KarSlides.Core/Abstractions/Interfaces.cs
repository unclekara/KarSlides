using KarSlides.Core.Model;

namespace KarSlides.Core.Abstractions;

/// <summary>
/// PowerPoint behind an interface. The real implementation marshals every call to one STA thread;
/// tests use a fake. All calls may throw <see cref="TimeoutException"/> when PowerPoint does not answer.
/// </summary>
public interface IPresentationHost
{
    Task<SlotHandle> OpenAsync(string path, int slotIndex, CancellationToken ct);

    /// <summary>Starts the show full screen on the monitor, without Presenter View and without taking focus.</summary>
    Task StartShowAsync(SlotHandle handle, MonitorTarget monitor, int startSlide, CancellationToken ct);

    Task NextAsync(SlotHandle handle, CancellationToken ct);
    Task PreviousAsync(SlotHandle handle, CancellationToken ct);
    Task GotoSlideAsync(SlotHandle handle, int slide, CancellationToken ct);

    /// <summary>Slide + number of clicks already played (0 = initial state of the slide).</summary>
    Task GotoClickAsync(SlotHandle handle, int slide, int click, CancellationToken ct);

    Task<HostPosition> GetPositionAsync(SlotHandle handle, CancellationToken ct);

    /// <summary>Ends the show and closes the presentation.</summary>
    Task CloseAsync(SlotHandle handle, CancellationToken ct);

    /// <summary>Pre-flight data of an opened presentation: fonts, linked files, media, Morph, slide size.</summary>
    Task<Preflight.PresentationInfo> InspectAsync(SlotHandle handle, CancellationToken ct);

    /// <summary>Exports one slide (final state) as PNG. Called while loading, before the show starts.</summary>
    Task ExportThumbnailAsync(SlotHandle handle, int slide, string path, int width, int height, CancellationToken ct);
}

/// <summary>
/// One BGRA frame. <see cref="Data"/> is valid only during the <see cref="IFrameSource.FrameArrived"/> call:
/// the consumer copies it.
/// </summary>
public readonly record struct VideoFrame(IntPtr Data, int Width, int Height, int Stride, long Timestamp);

public delegate void FrameArrivedHandler(in VideoFrame frame);

/// <summary>Frame producer: WGC monitor capture or a test pattern.</summary>
public interface IFrameSource : IDisposable
{
    event FrameArrivedHandler? FrameArrived;

    void Start();
}

/// <summary>Constant frame rate output (NDI). Never sends black except in <see cref="OutputMode.Black"/>.</summary>
public interface IOutputSink : IDisposable
{
    /// <summary>Copies the frame; the newest one is sent on the next tick.</summary>
    void PushVideo(in VideoFrame frame);

    OutputMode Mode { get; set; }
}
