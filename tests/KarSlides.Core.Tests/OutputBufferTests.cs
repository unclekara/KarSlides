using KarSlides.Core.Model;
using KarSlides.Ndi;
using KarSlides.Ndi.Output;

namespace KarSlides.Core.Tests;

public class FrameExchangeTests
{
    /// <summary>
    /// Async NDI send keeps the buffer of the last send call until the next call. The producer must never be
    /// handed (BackBuffer) the buffer NDI may still read, nor the current front.
    /// </summary>
    [Fact]
    public void ProducerNeverWritesIntoBufferHeldByNdi()
    {
        using var ex = new FrameExchange(4, 2);
        var rnd = new Random(7);
        IntPtr? inNdi = null; // buffer of the last send call

        for (var step = 0; step < 20_000; step++)
        {
            if (rnd.Next(3) == 0)
            {
                // Consumer tick: take newest (maybe), then "send" front. NDI releases the previous buffer now.
                ex.TryTakeNewest();
                inNdi = ex.FrontBuffer;
            }
            else
            {
                Assert.NotEqual(inNdi, ex.BackBuffer);
                Assert.NotEqual(ex.FrontBuffer, ex.BackBuffer);
                ex.Publish(step);
            }
        }
    }

    [Fact]
    public void TakesNewestPublishedFrame()
    {
        using var ex = new FrameExchange(4, 2);
        Assert.False(ex.TryTakeNewest());

        ex.Publish(1);
        ex.Publish(2);
        ex.Publish(3);

        Assert.True(ex.TryTakeNewest());
        Assert.Equal(3, ex.FrontSourceTicks);
        Assert.False(ex.TryTakeNewest());
    }
}

public class FrameSelectorTests
{
    [Fact]
    public void NothingIsSentBeforeTheFirstRealFrame()
    {
        using var ex = new FrameExchange(4, 2);
        var sel = new FrameSelector(ex);

        Assert.Equal(TickKind.None, sel.Next(OutputMode.Live));
        Assert.Equal(TickKind.None, sel.Next(OutputMode.Freeze));
    }

    [Fact]
    public void FreezeAtStart_IgnoresPublishedFrames_UntilLive()
    {
        // The slot starts frozen: the virtual desktop captured before the show must not go on air.
        using var ex = new FrameExchange(4, 2);
        var sel = new FrameSelector(ex);
        ex.Publish(1);

        Assert.Equal(TickKind.None, sel.Next(OutputMode.Freeze));
        ex.Publish(2);
        Assert.Equal(TickKind.Fresh, sel.Next(OutputMode.Live));
        Assert.Equal(2, ex.FrontSourceTicks);
    }

    [Fact]
    public void LiveRepeatsLastFrameWhenNothingNew()
    {
        using var ex = new FrameExchange(4, 2);
        var sel = new FrameSelector(ex);
        ex.Publish(1);

        Assert.Equal(TickKind.Fresh, sel.Next(OutputMode.Live));
        Assert.Equal(TickKind.Repeat, sel.Next(OutputMode.Live));
        Assert.Equal(TickKind.Repeat, sel.Next(OutputMode.Live));
    }

    [Fact]
    public void FreezeHoldsLastFrame_ThenLiveJumpsToNewest()
    {
        using var ex = new FrameExchange(4, 2);
        var sel = new FrameSelector(ex);
        ex.Publish(1);
        sel.Next(OutputMode.Live);

        ex.Publish(2);
        ex.Publish(3);
        Assert.Equal(TickKind.Repeat, sel.Next(OutputMode.Freeze));
        Assert.Equal(1, ex.FrontSourceTicks);

        Assert.Equal(TickKind.Fresh, sel.Next(OutputMode.Live));
        Assert.Equal(3, ex.FrontSourceTicks);
    }

    [Fact]
    public void BlackIsSentEvenWithoutFrames_AndOnlyInBlackMode()
    {
        using var ex = new FrameExchange(4, 2);
        var sel = new FrameSelector(ex);

        Assert.Equal(TickKind.Black, sel.Next(OutputMode.Black));
        ex.Publish(1);
        Assert.Equal(TickKind.Black, sel.Next(OutputMode.Black));
        Assert.Equal(TickKind.Fresh, sel.Next(OutputMode.Live));
    }
}

public class TestPatternMarkerTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(255)]
    [InlineData(4095)]
    [InlineData(4096)]
    [InlineData(123_456)]
    public void MarkerRoundTrips(long n) =>
        Assert.Equal((int)(n % 4096), TestPatternSource.DecodeMarker(TestPatternSource.EncodeMarker(n)));

    [Fact]
    public void MarkerSurvivesSmallCompressionError()
    {
        // +7 per channel (lossy codec) must decode to the same number.
        var noisy = TestPatternSource.EncodeMarker(1234) + 0x00070707u;
        Assert.Equal(1234, TestPatternSource.DecodeMarker(noisy));
    }
}
