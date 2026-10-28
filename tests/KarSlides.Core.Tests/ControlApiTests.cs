using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KarSlides.Control;
using KarSlides.Core.Configuration;
using KarSlides.Core.Model;
using KarSlides.Core.Slots;
using KarSlides.Core.Tests.Fakes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging.Abstractions;

namespace KarSlides.Core.Tests;

public sealed class ControlApiTests : IAsyncLifetime
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"karslides-api-{Guid.NewGuid():N}.pptx");
    private readonly FakePresentationHost _host = new(0, 2, 0);
    private readonly FakeOutputSink _output = new();
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    private Task StartAsync(string? token = null)
    {
        var slot = new SlotController(1, _host, _output, () => new MonitorTarget(@"\\.\DISPLAY5", 1920, 0, 1920, 1080),
            new SlotOptions { HoldRelease = TimeSpan.Zero }, NullLogger.Instance);
        var status = new AppStatus();
        status.AddProblem("test problem");
        _app = ControlServer.Create(new ControlOptions { Token = token }, new SlotSet([slot]), status,
            b => b.WebHost.UseTestServer());
        return _app.StartAsync();
    }

    public async Task InitializeAsync()
    {
        File.WriteAllText(_file, "fake");
        await StartAsync();
        _http = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        File.Delete(_file);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private Task<HttpResponseMessage> Load(int start = 1) =>
        _http.PostAsJsonAsync("/api/slots/1/load", new { path = _file, startSlide = start });

    [Fact]
    public async Task State_ListsSlotsAndProblems()
    {
        var r = await _http.GetAsync("/api/state");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await Json(r);
        Assert.Equal("empty", j.GetProperty("slots")[0].GetProperty("state").GetString());
        Assert.Equal("test problem", j.GetProperty("problems")[0].GetString());
    }

    [Fact]
    public async Task Load_ThenNavigate_ReturnsSlotStatus()
    {
        var r = await Load();
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await Json(r);
        Assert.Equal("ready", j.GetProperty("state").GetString());
        Assert.Equal(3, j.GetProperty("slideCount").GetInt32());
        Assert.Equal("live", j.GetProperty("output").GetString());

        j = await Json(await _http.PostAsync("/api/slots/1/next", null));
        Assert.Equal(2, j.GetProperty("slide").GetInt32());
        j = await Json(await _http.PostAsync("/api/slots/1/next", null));
        Assert.Equal(1, j.GetProperty("click").GetInt32());
    }

    [Theory]
    [InlineData("next")]
    [InlineData("prev")]
    [InlineData("first")]
    [InlineData("last")]
    [InlineData("black")]
    [InlineData("unblack")]
    [InlineData("freeze")]
    [InlineData("unfreeze")]
    public async Task Commands_AreAvailableAsGetForVmixScripts(string command)
    {
        await Load();
        var r = await _http.GetAsync($"/api/slots/1/{command}");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Goto_PostAndGet()
    {
        await Load();

        var j = await Json(await _http.PostAsJsonAsync("/api/slots/1/goto", new { slide = 2, click = 1 }));
        Assert.Equal((2, 1), (j.GetProperty("slide").GetInt32(), j.GetProperty("click").GetInt32()));

        j = await Json(await _http.GetAsync("/api/slots/1/goto?slide=3"));
        Assert.Equal((3, 0), (j.GetProperty("slide").GetInt32(), j.GetProperty("click").GetInt32()));
    }

    [Fact]
    public async Task BlackAndFreeze_ChangeOutput()
    {
        await Load();
        Assert.Equal("black", (await Json(await _http.PostAsync("/api/slots/1/black", null))).GetProperty("output").GetString());
        Assert.Equal(OutputMode.Black, _output.Mode);
        Assert.Equal("live", (await Json(await _http.PostAsync("/api/slots/1/unblack", null))).GetProperty("output").GetString());
        Assert.Equal("freeze", (await Json(await _http.PostAsync("/api/slots/1/freeze", null))).GetProperty("output").GetString());
    }

    [Fact]
    public async Task Notes_AreServedPerSlide_AndClearedOnUnload()
    {
        _host.Info = _host.Info with { Notes = ["intro", "", "line 1\nline 2"] };
        await Load();

        var j = await Json(await _http.GetAsync("/api/slots/1/notes"));
        Assert.Equal(["intro", "", "line 1\nline 2"], j.GetProperty("notes").EnumerateArray().Select(x => x.GetString()));

        await _http.PostAsync("/api/slots/1/unload", null);
        j = await Json(await _http.GetAsync("/api/slots/1/notes"));
        Assert.Equal(0, j.GetProperty("notes").GetArrayLength());
    }

    [Fact]
    public async Task Unload()
    {
        await Load();
        var j = await Json(await _http.PostAsync("/api/slots/1/unload", null));
        Assert.Equal("empty", j.GetProperty("state").GetString());
    }

    [Fact]
    public async Task NavigationWhenEmpty_Is409()
    {
        var r = await _http.PostAsync("/api/slots/1/next", null);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Contains("Empty", (await Json(r)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task UnknownSlot_Is404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/api/slots/2")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.PostAsync("/api/slots/2/next", null)).StatusCode);
    }

    [Fact]
    public async Task BadRequests_Are400()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.PostAsJsonAsync("/api/slots/1/load", new { path = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _http.PostAsJsonAsync("/api/slots/1/load", new { path = _file + ".missing" })).StatusCode);
        await Load();
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.GetAsync("/api/slots/1/goto?slide=99")).StatusCode);
    }

    [Fact]
    public async Task PowerPointTimeout_Is504_AndSlotGoesToError()
    {
        await Load();
        _host.ThrowOnNext = true;

        var r = await _http.PostAsync("/api/slots/1/next", null);

        Assert.Equal(HttpStatusCode.GatewayTimeout, r.StatusCode);
        var j = await Json(await _http.GetAsync("/api/slots/1"));
        Assert.Equal("error", j.GetProperty("state").GetString());
        Assert.Equal("freeze", j.GetProperty("output").GetString());
    }

    [Fact]
    public async Task Token_IsRequiredWhenConfigured()
    {
        await _app.DisposeAsync();
        await StartAsync(token: "s3cret");
        var http = _app.GetTestClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/state")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/state?token=s3cret")).StatusCode);

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/state");
        req.Headers.Add(ControlServer.TokenHeader, "s3cret");
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(req)).StatusCode);
    }
}
