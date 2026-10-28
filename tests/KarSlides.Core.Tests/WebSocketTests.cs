using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using KarSlides.Control;
using KarSlides.Core.Configuration;
using KarSlides.Core.Model;
using KarSlides.Core.Slots;
using KarSlides.Core.Tests.Fakes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace KarSlides.Core.Tests;

public sealed class WebSocketTests : IAsyncLifetime
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"karslides-ws-{Guid.NewGuid():N}.pptx");
    private readonly FakePresentationHost _host = new(0, 1, 0);
    private SlotController _slot = null!;
    private WebApplication _app = null!;
    private WebSocket _ws = null!;

    public async Task InitializeAsync()
    {
        File.WriteAllText(_file, "x");
        _slot = new SlotController(1, _host, new FakeOutputSink(), () => new MonitorTarget(@"\\.\DISPLAY5", 1920, 0, 1920, 1080),
            new SlotOptions { HoldRelease = TimeSpan.Zero }, NullLogger.Instance);
        _app = ControlServer.Create(new ControlOptions(), new SlotSet([_slot]), new AppStatus(), b => b.WebHost.UseTestServer());
        await _app.StartAsync();
        _ws = await _app.GetTestServer().CreateWebSocketClient().ConnectAsync(new Uri("ws://localhost/ws"), default);
    }

    public async Task DisposeAsync()
    {
        _ws.Dispose();
        await _app.DisposeAsync();
        File.Delete(_file);
    }

    private async Task<JsonElement> NextEvent(string type)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var r = await _ws.ReceiveAsync(buffer, cts.Token);
            var e = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, r.Count)).RootElement;
            if (e.GetProperty("type").GetString() == type) return e.GetProperty("data");
        }
    }

    [Fact]
    public async Task FirstMessage_IsFullState()
    {
        var data = await NextEvent("state");
        Assert.Equal("empty", data.GetProperty("slots")[0].GetProperty("state").GetString());
    }

    [Fact]
    public async Task Load_AndNext_ProduceSlotStateAndPositionEvents()
    {
        await NextEvent("state");
        await _slot.LoadAsync(_file, 1, default);
        Assert.Equal("loading", (await NextEvent("slotState")).GetProperty("state").GetString());
        Assert.Equal("ready", (await NextEvent("slotState")).GetProperty("state").GetString());

        await _slot.NextAsync(default);
        var pos = await NextEvent("position");
        Assert.Equal((2, 0), (pos.GetProperty("slide").GetInt32(), pos.GetProperty("click").GetInt32()));
    }

    [Fact]
    public async Task Tally_AndError_AreSeparateEvents()
    {
        await NextEvent("state");
        _slot.SetTally(new Tally(true, false));
        Assert.True((await NextEvent("tally")).GetProperty("onProgram").GetBoolean());

        await _slot.LoadAsync(_file, 1, default, force: true);
        _host.ThrowOnNext = true;
        await Assert.ThrowsAsync<TimeoutException>(() => _slot.NextAsync(default));
        Assert.Contains("timed out", (await NextEvent("error")).GetProperty("message").GetString());
    }

    [Fact]
    public async Task RecoveryAndLog_AreBroadcast()
    {
        await NextEvent("state");
        var hub = _app.Services.GetRequiredService<EventHub>();
        hub.PublishRecovery(new RecoveryReport("test", TimeSpan.FromMilliseconds(1800), true, []));
        hub.PublishLog("Warning", "test", "hello");

        Assert.Equal(1800, (await NextEvent("recovery")).GetProperty("durationMs").GetInt32());
        Assert.Equal("hello", (await NextEvent("log")).GetProperty("message").GetString());
    }
}
