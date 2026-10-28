using System.Diagnostics;
using KarSlides.Core.Abstractions;
using KarSlides.Core.Model;
using KarSlides.Core.Preflight;
using KarSlides.Core.Slots;
using Microsoft.Extensions.Logging;

namespace KarSlides.PowerPoint;

/// <summary>
/// The current <see cref="PowerPointHost"/> behind a stable <see cref="IPresentationHost"/> for the slots, plus
/// the watchdog's restart: kill POWERPNT.EXE, abandon the old host and its STA thread (possibly stuck
/// in a hung call), create a new host. PowerPoint itself starts again on the next open.
/// </summary>
public sealed class PowerPointRuntime(Func<PowerPointHost> createHost, ILogger<PowerPointRuntime> log)
    : IPresentationHost, IPresentationRuntime, IDisposable
{
    private readonly object _sync = new();
    private PowerPointHost _host = createHost();

    private PowerPointHost Host
    {
        get { lock (_sync) return _host; }
    }

    public bool IsProcessAlive => Process.GetProcessesByName("POWERPNT").Length > 0;

    public async Task RestartAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        foreach (var p in Process.GetProcessesByName("POWERPNT"))
        {
            try
            {
                log.LogWarning("Killing POWERPNT.EXE pid {Pid}", p.Id);
                p.Kill();
                await p.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Could not kill POWERPNT.EXE pid {Pid}", p.Id);
            }
            finally
            {
                p.Dispose();
            }
        }
        log.LogInformation("POWERPNT.EXE gone after {Ms} ms", sw.ElapsedMilliseconds);

        PowerPointHost old;
        lock (_sync)
        {
            old = _host;
            _host = createHost();
        }
        old.Abandon();
    }

    public Task<SlotHandle> OpenAsync(string path, int slotIndex, CancellationToken ct) => Host.OpenAsync(path, slotIndex, ct);
    public Task StartShowAsync(SlotHandle h, MonitorTarget m, int startSlide, CancellationToken ct) => Host.StartShowAsync(h, m, startSlide, ct);
    public Task NextAsync(SlotHandle h, CancellationToken ct) => Host.NextAsync(h, ct);
    public Task PreviousAsync(SlotHandle h, CancellationToken ct) => Host.PreviousAsync(h, ct);
    public Task GotoSlideAsync(SlotHandle h, int slide, CancellationToken ct) => Host.GotoSlideAsync(h, slide, ct);
    public Task GotoClickAsync(SlotHandle h, int slide, int click, CancellationToken ct) => Host.GotoClickAsync(h, slide, click, ct);
    public Task<HostPosition> GetPositionAsync(SlotHandle h, CancellationToken ct) => Host.GetPositionAsync(h, ct);
    public Task CloseAsync(SlotHandle h, CancellationToken ct) => Host.CloseAsync(h, ct);
    public Task<PresentationInfo> InspectAsync(SlotHandle h, CancellationToken ct) => Host.InspectAsync(h, ct);

    public Task ExportThumbnailAsync(SlotHandle h, int slide, string path, int width, int height, CancellationToken ct) =>
        Host.ExportThumbnailAsync(h, slide, path, width, height, ct);

    public void Dispose() => Host.Dispose();
}
