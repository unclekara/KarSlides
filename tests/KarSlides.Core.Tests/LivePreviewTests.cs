using System.Runtime.InteropServices;
using System.Text;
using KarSlides.Control;
using KarSlides.Core.Abstractions;
using KarSlides.Core.Configuration;
using KarSlides.Core.Model;
using KarSlides.Core.Preview;
using KarSlides.Core.Slots;
using KarSlides.Core.Tests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging.Abstractions;

namespace KarSlides.Core.Tests;

public sealed class LivePreviewTests : IDisposable
{
    private const int W = 64, H = 36;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(3);
    private readonly FakeOutputSink _inner = new();
    private readonly FakeEncoder _encoder = new();
    private readonly LivePreview _preview;

    public LivePreviewTests()
    {
        _preview = new LivePreview(_inner, _encoder, new LivePreviewOptions { Fps = 100, Width = W / 2 }, NullLogger.Instance);
        _preview.Mode = OutputMode.Live;
    }

    public void Dispose() => _preview.Dispose();

    /// <summary>Encodes the colour of the first pixel, so a test can tell which frame a picture came from.</summary>
    private sealed class FakeEncoder : IPreviewEncoder
    {
        public int Calls;

        public byte[] Encode(byte[] bgra, int width, int height, int quality)
        {
            Interlocked.Increment(ref Calls);
            return [bgra[2], bgra[1], bgra[0], (byte)width, (byte)height]; // r, g, b, size
        }
    }

    private void Push(byte r, byte g, byte b)
    {
        var stride = W * 4 + 16; // padded rows, as captured frames may have
        var data = Marshal.AllocHGlobal(stride * H);
        try
        {
            var row = new byte[stride];
            for (var x = 0; x < W; x++) (row[x * 4], row[x * 4 + 1], row[x * 4 + 2], row[x * 4 + 3]) = (b, g, r, 255);
            for (var y = 0; y < H; y++) Marshal.Copy(row, 0, data + y * stride, stride);
            _preview.PushVideo(new VideoFrame(data, W, H, stride, 0));
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    private async Task<PreviewImage> NextAsync(long after = -1) =>
        await _preview.WaitNewerAsync(after, Wait, default) ?? throw new TimeoutException("no preview picture");

    [Fact]
    public void FramesAndModes_GoToTheOutputUnchanged()
    {
        Push(1, 2, 3);
        _preview.Mode = OutputMode.Black;
        Assert.Equal(1, _inner.Frames);
        Assert.Equal(OutputMode.Black, _inner.Mode);
    }

    [Fact]
    public async Task WithoutWatchers_NothingIsEncoded()
    {
        Push(10, 20, 30);
        Assert.Null(await _preview.WaitNewerAsync(-1, TimeSpan.FromMilliseconds(300), default));
        Assert.Equal(0, _encoder.Calls);
    }

    [Fact]
    public async Task WatcherOnStaticPicture_GetsTheLastFrame_ScaledDown()
    {
        Push(10, 20, 30);
        Push(40, 50, 60); // then the picture stops changing (a static slide sends no frames)

        using var watch = _preview.Watch();
        var img = await NextAsync();

        Assert.Equal(new byte[] { 40, 50, 60, W / 2, H / 2 }, img.Jpeg);
    }

    [Fact]
    public async Task Black_ShowsBlack_AndBackToLive_ShowsThePictureAgain()
    {
        using var watch = _preview.Watch();
        Push(40, 50, 60);
        var live = await NextAsync();

        _preview.Mode = OutputMode.Black;
        var black = await NextAsync(live.Sequence);
        Assert.Equal(new byte[] { 0, 0, 0 }, black.Jpeg[..3]);

        _preview.Mode = OutputMode.Live;
        var again = await NextAsync(black.Sequence);
        Assert.Equal(new byte[] { 40, 50, 60 }, again.Jpeg[..3]);
    }

    [Fact]
    public async Task Freeze_KeepsTheFrozenPicture()
    {
        using var watch = _preview.Watch();
        Push(40, 50, 60);
        var before = await NextAsync();

        _preview.Mode = OutputMode.Freeze;
        Push(90, 90, 90);
        var after = await _preview.WaitNewerAsync(before.Sequence, TimeSpan.FromMilliseconds(300), default);

        Assert.True(after is null || after.Jpeg[0] == 40, "frames during Freeze must not reach the preview");
    }

    [Fact]
    public async Task FastFrames_AreThinnedOut_AndTheNewestWins()
    {
        using var slow = new LivePreview(new FakeOutputSink(), _encoder, new LivePreviewOptions { Fps = 5, Width = W }, NullLogger.Instance);
        slow.Mode = OutputMode.Live;
        using var watch = slow.Watch();
        var stride = W * 4;
        var data = Marshal.AllocHGlobal(stride * H);
        try
        {
            for (byte i = 1; i <= 60; i++)
            {
                var row = Enumerable.Repeat(i, stride).ToArray();
                for (var y = 0; y < H; y++) Marshal.Copy(row, 0, data + y * stride, stride);
                slow.PushVideo(new VideoFrame(data, W, H, stride, 0));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }

        await Task.Delay(700);
        var last = slow.Latest!;
        Assert.InRange(_encoder.Calls, 1, 5); // 60 frames, ~0.7 s at 5 fps
        Assert.Equal(60, last.Jpeg[0]);
    }

    [Fact]
    public void Downscale_AveragesBlocks()
    {
        // 2x2 -> 1x1: average of four pixels.
        byte[] src = [0, 0, 0, 255, 100, 100, 100, 255, 200, 200, 200, 255, 100, 100, 100, 255];
        var dst = new byte[4];
        PreviewScaler.Downscale(src, 2, 2, 8, dst, 1, 1);
        Assert.Equal(new byte[] { 100, 100, 100, 255 }, dst);
    }

    [Fact]
    public async Task Api_StreamsMjpeg()
    {
        var previews = new PreviewSet();
        previews.Add(1, _preview);
        await using var app = ControlServer.Create(new ControlOptions(), new SlotSet([]), new AppStatus(),
            b => b.WebHost.UseTestServer(), previews: previews);
        await app.StartAsync();
        var http = app.GetTestClient();
        Push(40, 50, 60);

        using var cts = new CancellationTokenSource(Wait);
        using var response = await http.GetAsync("/api/slots/1/live.mjpg", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.StartsWith("multipart/x-mixed-replace", response.Content.Headers.ContentType!.ToString());
        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        var text = new StringBuilder();
        var buffer = new byte[256];
        while (!text.ToString().Contains("Content-Length: 5"))
        {
            var n = await stream.ReadAsync(buffer, cts.Token);
            if (n == 0) break;
            text.Append(Encoding.Latin1.GetString(buffer, 0, n));
        }
        Assert.Contains("--karslidesframe", text.ToString());
        Assert.Contains("Content-Type: image/jpeg", text.ToString());
        Assert.Equal(1, _preview.Viewers);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await http.GetAsync("/api/slots/2/live.mjpg")).StatusCode);
    }
}
