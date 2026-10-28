using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using KarSlides.App;
using KarSlides.Control;
using KarSlides.Core.Configuration;
using KarSlides.Core.Model;
using KarSlides.Core.Slots;
using KarSlides.Core.Tests.Fakes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace KarSlides.Core.Tests;

/// <summary>Settings page API: read, write settings.json, restart.</summary>
public sealed class SettingsPageTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"karslides-settings-{Guid.NewGuid():N}");
    private string BaseFile => Path.Combine(_dir, "appsettings.json");
    private string UserFile => Path.Combine(_dir, "user", "settings.json");
    private SlotController _slot = null!;
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private int _restarts;

    private static KarSlidesOptions Load(params string[] files)
    {
        var b = new ConfigurationBuilder();
        foreach (var f in files) b.AddJsonFile(f, optional: true);
        var o = new KarSlidesOptions();
        Program.BindOptions(b.Build(), o);
        return o;
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(BaseFile, """{ "Preview": { "Fps": 25 }, "Output": { "Fps": "50" }, "Clicker": { "Next": [ "PageDown" ] } }""");
        _slot = new SlotController(1, new FakePresentationHost(0), new FakeOutputSink(),
            () => new MonitorTarget(@"\\.\DISPLAY5", 1920, 0, 1920, 1080), new SlotOptions(), NullLogger.Instance);
        var ctx = new SettingsContext(UserFile, Load(BaseFile, UserFile), () => Load(BaseFile, UserFile), () => Load(BaseFile),
            () => Interlocked.Increment(ref _restarts));
        _app = ControlServer.Create(new ControlOptions(), new SlotSet([_slot]), new AppStatus(), b => b.WebHost.UseTestServer(),
            settings: ctx);
        await _app.StartAsync();
        _http = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        Directory.Delete(_dir, recursive: true);
    }

    private async Task<JsonElement> Get() => JsonDocument.Parse(await _http.GetStringAsync("/api/settings")).RootElement;

    private static JsonElement Field(JsonElement settings, string path) =>
        settings.GetProperty("fields").EnumerateArray().First(f => f.GetProperty("path").GetString() == path);

    private Task<HttpResponseMessage> Put(object values) => _http.PutAsJsonAsync("/api/settings", new { values });

    [Fact]
    public async Task Read_ShowsCurrentAndSaved_NoRestartNeeded()
    {
        var s = await Get();
        Assert.False(s.GetProperty("restartNeeded").GetBoolean());
        var fps = Field(s, "Preview.Fps");
        Assert.Equal(25, fps.GetProperty("current").GetDouble());
        Assert.Equal("number", fps.GetProperty("kind").GetString());
        Assert.Equal("50", Field(s, "Output.Fps").GetProperty("saved").GetString());
    }

    [Fact]
    public async Task Write_StoresOnlyDifferences_KeepsOtherKeys_AndAsksForRestart()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(UserFile)!);
        File.WriteAllText(UserFile, """{ "Custom": { "Keep": 1 }, "preview": { "quality": 60 } }""");

        var r = await Put(new Dictionary<string, object>
        {
            ["Preview.Fps"] = 10, ["Preview.Width"] = "640", ["Output.Fps"] = "50", ["Clicker.Next"] = "PageDown, Right",
        });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        var file = JsonNode.Parse(File.ReadAllText(UserFile))!.AsObject();
        Assert.Equal(1, (int)file["Custom"]!["Keep"]!);
        Assert.Equal(10, (double)file["preview"]!["Fps"]!); // existing spelling of the section reused
        Assert.Equal(640, (int)file["preview"]!["Width"]!);
        Assert.Equal(60, (int)file["preview"]!["quality"]!); // not sent: untouched
        Assert.Null(file["Output"]); // equal to appsettings.json: not written
        Assert.Equal(["PageDown", "Right"], file["Clicker"]!["Next"]!.AsArray().Select(x => (string)x!));

        var s = await Get();
        Assert.True(s.GetProperty("restartNeeded").GetBoolean());
        Assert.Equal(25, Field(s, "Preview.Fps").GetProperty("current").GetDouble());
        Assert.Equal(10, Field(s, "Preview.Fps").GetProperty("saved").GetDouble());
        Assert.True(File.Exists(UserFile + ".bak"));
    }

    [Fact]
    public async Task Write_BackToDefault_RemovesKeyAndEmptySection()
    {
        await Put(new Dictionary<string, object> { ["Preview.Fps"] = 10 });
        await Put(new Dictionary<string, object> { ["Preview.Fps"] = "25" });

        var file = JsonNode.Parse(File.ReadAllText(UserFile))!.AsObject();
        Assert.Null(file["Preview"]);
        Assert.False((await Get()).GetProperty("restartNeeded").GetBoolean());
    }

    [Theory]
    [InlineData("Clicker.Next", "PageDown, Банан")]
    [InlineData("Control.Port", 8099)]
    [InlineData("Preview.Fps", 500)]
    [InlineData("Output.Fps", "24")]
    [InlineData("Slots.NdiNameTemplate", "no slot number")]
    [InlineData("Unknown.Key", 1)]
    [InlineData("Audio.OutputDevice", "Speakers")]
    public async Task Write_InvalidValue_Is400_AndFileUntouched(string path, object value)
    {
        var r = await Put(new Dictionary<string, object> { ["Preview.Quality"] = 50, [path] = value });

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.False(File.Exists(UserFile)); // nothing written, not even the valid value
    }

    [Fact]
    public async Task AudioDevice_IsStoredById_AndDefaultRemovesIt()
    {
        const string id = "{0.0.0.00000000}.{370675d2-c50d-4bbd-bd4e-93e0d6cb77de}";
        Assert.Equal("audioDevice", Field(await Get(), "Audio.OutputDevice").GetProperty("kind").GetString());

        Assert.Equal(HttpStatusCode.OK, (await Put(new Dictionary<string, object> { ["Audio.OutputDevice"] = " " + id })).StatusCode);
        Assert.Equal(id, (string)JsonNode.Parse(File.ReadAllText(UserFile))!["Audio"]!["OutputDevice"]!);

        await Put(new Dictionary<string, object> { ["Audio.OutputDevice"] = "" });
        Assert.Null(JsonNode.Parse(File.ReadAllText(UserFile))!["Audio"]);
    }

    [Fact]
    public void EveryField_HasAnEnglishText() => Assert.Empty(SettingsCatalog.MissingEnglish);

    [Fact]
    public async Task English_LabelsAndValidationMessages()
    {
        var s = JsonDocument.Parse(await _http.GetStringAsync("/api/settings?lang=en")).RootElement;
        var fps = Field(s, "Output.Fps");
        Assert.Equal("Frame rate", fps.GetProperty("label").GetString());
        Assert.Equal("NDI® output", fps.GetProperty("group").GetString());
        Assert.Equal("Частота кадров", Field(await Get(), "Output.Fps").GetProperty("label").GetString()); // Russian by default

        var r = await _http.PutAsJsonAsync("/api/settings?lang=en", new { values = new Dictionary<string, object> { ["Control.Port"] = 8099 } });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("ports 8088 and 8099 belong to vMix", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Restart_OnProgram_NeedsForce()
    {
        _slot.SetTally(new Tally(true, false));

        Assert.Equal(HttpStatusCode.Conflict, (await _http.PostAsync("/api/restart", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await _http.PostAsync("/api/restart?force=true", null)).StatusCode);
        await Task.Delay(800);
        Assert.Equal(1, _restarts);
    }
}
