using KarSlides.Core.Audio;
using KarSlides.Core.Configuration;

namespace KarSlides.Core.Tests;

public class AudioRingTests
{
    private const int Rate = 48000;

    private static float[] Packet(int frames, float value)
    {
        var p = new float[frames * 2];
        Array.Fill(p, value);
        return p;
    }

    [Fact]
    public void EmptyRing_ReturnsSilence_OfExactLength()
    {
        var ring = new AudioRing(Rate, 2);
        var planar = new float[960 * 2];
        Array.Fill(planar, 9f);

        ring.ReadPlanar(planar, 960);

        Assert.All(planar, s => Assert.Equal(0f, s));
        Assert.Equal(0, ring.UnderrunFrames); // silence during pre-roll is not an underrun
    }

    [Fact]
    public void PreRoll_WaitsForTargetLevel_ThenDeliversInOrderAsPlanar()
    {
        var ring = new AudioRing(Rate, 2, targetMs: 20);
        var interleaved = new float[960 * 2];
        for (var i = 0; i < 960; i++) (interleaved[2 * i], interleaved[2 * i + 1]) = (i, -i);

        ring.Write(interleaved.AsSpan(0, 480 * 2)); // 10 ms < target
        var planar = new float[960 * 2];
        ring.ReadPlanar(planar, 960);
        Assert.Equal(0f, planar[0] + planar[959]); // still pre-roll

        ring.Write(interleaved.AsSpan(480 * 2)); // now 20 ms
        ring.ReadPlanar(planar, 960);
        Assert.Equal(0f, planar[0]);
        Assert.Equal(959f, planar[959]);        // left channel first
        Assert.Equal(-959f, planar[960 + 959]); // then right channel
    }

    [Fact]
    public void SteadyState_10msPacketsAgainst20msReads_NoUnderrunsNoTrims()
    {
        var ring = new AudioRing(Rate, 2);
        var planar = new float[960 * 2];
        for (var tick = 0; tick < 3000; tick++) // 60 s
        {
            ring.Write(Packet(480, 0.5f));
            ring.Write(Packet(480, 0.5f));
            ring.ReadPlanar(planar, 960);
        }
        Assert.Equal(0, ring.UnderrunFrames);
        Assert.Equal(0, ring.Trims);
    }

    [Fact]
    public void BacklogFromAStall_IsTrimmedWithinAboutOneSecond()
    {
        var ring = new AudioRing(Rate, 2);
        var planar = new float[960 * 2];
        // Output stalled for 100 ms while capture kept writing: 100 ms of extra latency.
        for (var i = 0; i < 10; i++) ring.Write(Packet(480, 0.1f));
        ring.Write(Packet(960, 0.1f)); // pre-roll satisfied

        for (var tick = 0; tick < 60; tick++)
        {
            ring.Write(Packet(960, 0.1f));
            ring.ReadPlanar(planar, 960);
        }

        Assert.True(ring.Trims >= 1);
        Assert.True(ring.Level <= 960 + ring.SafetyFrames + Rate / 200, $"level {ring.Level}");
    }

    [Fact]
    public void Reset_ClearsAndRequiresPreRollAgain()
    {
        var ring = new AudioRing(Rate, 2);
        ring.Write(Packet(1920, 1f));
        ring.Reset();
        Assert.Equal(0, ring.Level);

        var planar = new float[960 * 2];
        ring.Write(Packet(480, 1f));
        ring.ReadPlanar(planar, 960);
        Assert.All(planar, s => Assert.Equal(0f, s));
    }
}

public class AppStatusWarningTests
{
    [Fact]
    public void Warning_AppearsAndClears_ProblemsStay()
    {
        var s = new KarSlides.Core.Slots.AppStatus();
        s.AddProblem("NDI missing");
        s.SetWarning("audio-enhancements", "enhancements on");
        Assert.Equal(["NDI missing", "enhancements on"], s.Problems);

        s.SetWarning("audio-enhancements", null);
        Assert.Equal(["NDI missing"], s.Problems);
    }
}

public class SamplesPerFrameTests
{
    [Theory]
    [InlineData("50", 960)]
    [InlineData("25", 1920)]
    [InlineData("60", 800)]
    public void IntegerRates_HaveConstantSampleCount(string fps, int expected)
    {
        var r = FrameRate.Parse(fps);
        for (var k = 0; k < 100; k++) Assert.Equal(expected, r.SamplesInFrame(k, 48000));
    }

    [Theory]
    [InlineData("29.97", 30000)]
    [InlineData("59.94", 60000)]
    public void NtscRates_SumExactlyWithoutDrift(string fps, int frames)
    {
        // 30000 frames at 29.97 = 1001 s = 48 048 000 samples exactly.
        var r = FrameRate.Parse(fps);
        long total = 0;
        for (var k = 0; k < frames; k++) total += r.SamplesInFrame(k, 48000);
        Assert.Equal(48000L * frames * r.Denominator / r.Numerator, total);
    }
}
