using KarSlides.Core.Model;

namespace KarSlides.Ndi.Output;

/// <summary>What the output sends on one tick.</summary>
internal enum TickKind
{
    /// <summary>Nothing yet: no real frame was ever taken (receiver shows "no signal", never black).</summary>
    None,
    Fresh,
    Repeat,
    Black,
}

/// <summary>
/// Per-tick decision of the paced output, without threads or timers (unit-tested):
/// Live takes the newest published frame or repeats; Freeze repeats without taking (the producer keeps
/// publishing, the newest frame is picked up on return to Live); Black sends the black buffer.
/// <see cref="FrameExchange.TryTakeNewest"/> is called only here, right before a send call (async rule).
/// </summary>
internal sealed class FrameSelector(FrameExchange frames)
{
    private bool _hasFrame;

    public TickKind Next(OutputMode mode)
    {
        switch (mode)
        {
            case OutputMode.Black:
                return TickKind.Black;
            case OutputMode.Live when frames.TryTakeNewest():
                _hasFrame = true;
                return TickKind.Fresh;
            default:
                return _hasFrame ? TickKind.Repeat : TickKind.None;
        }
    }
}
