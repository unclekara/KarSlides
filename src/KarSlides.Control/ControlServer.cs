using System.Net;
using System.Text;
using System.Text.Json.Serialization;
using KarSlides.Core;
using KarSlides.Core.Clicker;
using KarSlides.Core.Configuration;
using KarSlides.Core.Model;
using KarSlides.Core.Preview;
using KarSlides.Core.Slots;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace KarSlides.Control;

public sealed record LoadRequest(string? Path, int? StartSlide, bool? Force = null);

public sealed record GotoRequest(int Slide, int? Click);

public sealed record StateResponse(IReadOnlyList<SlotStatus> Slots, IReadOnlyList<string> Problems, object? Vmix, ProductInfo Product);

public sealed record ErrorResponse(string Error);

public sealed record ClickerRequest(bool Enabled);

public sealed record ClickerResponse(bool Enabled, IReadOnlyList<string> Next, IReadOnlyList<string> Prev,
    IReadOnlyList<string> Black, IReadOnlyList<string> Swallow);

public sealed record InboxFile(string Name, string Path, long Size, DateTime Modified);

/// <summary>Paths the API and UI need: presentations inbox and the folder with the web UI files.</summary>
public sealed record ControlContext(string Inbox, string WebRoot);

/// <summary>
/// REST API. Kestrel on Control.Port (9180 by default, never 8088/8099).
/// Navigation and output commands are also available as GET for vMix scripts and simple controllers.
/// </summary>
public static class ControlServer
{
    public const string TokenHeader = "X-KarSlides-Token";

    /// <summary>Kestrel endpoint and JSON settings. SlotSet and AppStatus must be registered by the caller.</summary>
    public static void Configure(WebApplicationBuilder builder, ControlOptions options)
    {
        builder.WebHost.ConfigureKestrel(k =>
        {
            // "Any" listens on IPv4 and IPv6: clients resolving "localhost" to ::1 first would otherwise
            // wait ~200 ms for the refused IPv6 connection on every request.
            if (options.Bind is "0.0.0.0" or "*" or "::") k.ListenAnyIP(options.Port);
            else k.Listen(IPAddress.Parse(options.Bind), options.Port);
        });
        builder.Services.Configure<JsonOptions>(o =>
        {
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
        });
    }

    /// <summary>Token check, API routes and the /ws event stream. EventHub must be registered by the caller.</summary>
    public static void MapApi(WebApplication app, ControlOptions options)
    {
        if (!string.IsNullOrEmpty(options.Token)) app.Use(RequireToken(options.Token));
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
        MapWebUi(app);
        Map(app);
        app.Map("/ws", async (HttpContext ctx, EventHub hub, SlotSet slots, AppStatus status) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsJsonAsync(new ErrorResponse("WebSocket request expected"));
                return;
            }
            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            using var ending = UntilStopping(ctx);
            await hub.ServeAsync(socket, State(slots, status), ending.Token);
        });
    }

    /// <summary>
    /// Operator panel at "/" and presenter page at "/presenter/{n}": static HTML/JS/CSS from
    /// <see cref="ControlContext.WebRoot"/>, no build step. Not served when the folder is missing.
    /// </summary>
    private static void MapWebUi(WebApplication app)
    {
        var root = app.Services.GetRequiredService<ControlContext>().WebRoot;
        if (!Directory.Exists(root)) return;
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(root),
            // The UI changes with the app version; do not let the browser keep stale scripts.
            OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache",
        });
        IResult Page(string name) => Results.File(Path.Combine(root, name), "text/html; charset=utf-8");
        app.MapGet("/", () => Page("index.html"));
        app.MapGet("/presenter/{n:int}", (int n) => Page("presenter.html"));
        app.MapGet("/settings", () => Page("settings.html"));
    }

    /// <summary>Stand-alone API host (tests): slot changes are published to the hub.</summary>
    /// <param name="configure">Extra builder setup, e.g. TestServer.</param>
    public static WebApplication Create(ControlOptions options, SlotSet slots, AppStatus status,
        Action<WebApplicationBuilder>? configure = null, ControlContext? context = null, ClickerController? clicker = null,
        PreviewSet? previews = null, SettingsContext? settings = null, MonitorSource? monitors = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        Configure(builder, options);
        var hub = new EventHub();
        foreach (var s in slots.All) s.Changed += hub.PublishSlot;
        builder.Services.AddSingleton(slots);
        builder.Services.AddSingleton(status);
        builder.Services.AddSingleton(hub);
        builder.Services.AddSingleton(previews ?? new PreviewSet());
        if (settings is not null) builder.Services.AddSingleton(settings);
        if (monitors is not null) builder.Services.AddSingleton(monitors);
        builder.Services.AddSingleton(context ?? new ControlContext(
            Path.Combine(Path.GetTempPath(), "karslides-no-inbox"), Path.Combine(Path.GetTempPath(), "karslides-no-web")));
        builder.Services.AddSingleton(clicker ?? new ClickerController(new ClickerOptions { Enabled = false }, slots,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance));
        configure?.Invoke(builder);

        var app = builder.Build();
        MapApi(app, options);
        return app;
    }

    private static ClickerResponse Clicker(ClickerController c) =>
        new(c.Enabled, c.Keys.Next, c.Keys.Prev, c.Keys.Black, c.Keys.Swallow);

    private static StateResponse State(SlotSet slots, AppStatus status) =>
        new(slots.All.Select(s => s.Status).ToArray(), status.Problems, status.Vmix(), ProductInfo.Current);

    private static Func<HttpContext, RequestDelegate, Task> RequireToken(string token) => async (ctx, next) =>
    {
        var given = ctx.Request.Headers[TokenHeader].FirstOrDefault() ?? ctx.Request.Query["token"].FirstOrDefault();
        if (!string.Equals(given, token, StringComparison.Ordinal))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new ErrorResponse($"Token required: header {TokenHeader} or ?token="));
            return;
        }
        await next(ctx);
    };

    private static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/state", (SlotSet slots, AppStatus status) => State(slots, status));

        api.MapGet("/clicker", (ClickerController c) => Clicker(c));
        api.MapPost("/clicker", (ClickerRequest body, ClickerController c) =>
        {
            c.Enabled = body.Enabled;
            return Clicker(c);
        });

        // Settings page of the panel: read and write settings.json, restart to apply.
        api.MapGet("/settings", (HttpContext ctx) =>
            ctx.RequestServices.GetService<SettingsContext>() is { } s
                ? Results.Ok(SettingsStore.Read(s))
                : Results.NotFound(new ErrorResponse("Settings are not available")));
        api.MapPut("/settings", (SettingsUpdate body, HttpContext ctx) =>
        {
            if (ctx.RequestServices.GetService<SettingsContext>() is not { } s)
                return Results.NotFound(new ErrorResponse("Settings are not available"));
            try
            {
                SettingsStore.Write(s, body);
                return Results.Ok(SettingsStore.Read(s));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new ErrorResponse(ex.Message));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
            {
                return Results.Json(new ErrorResponse($"Cannot write {s.UserFile}: {ex.Message}"), statusCode: StatusCodes.Status500InternalServerError);
            }
        });
        api.MapGet("/monitors", (HttpContext ctx) =>
        {
            var source = ctx.RequestServices.GetService<MonitorSource>();
            var settings = ctx.RequestServices.GetService<SettingsContext>();
            if (source is null || settings is null) return Results.NotFound(new ErrorResponse("Monitor list is not available"));
            var current = settings.Current;
            var saved = settings.LoadSaved();
            return Results.Ok(MonitorChoices.Build(source.Enumerate(), current.Output.Width, current.Output.Height,
                current.Slots.Monitors.FirstOrDefault() ?? "virtual", saved.Slots.Monitors.FirstOrDefault() ?? "virtual"));
        });
        api.MapGet("/audio/devices", (HttpContext ctx) =>
        {
            var source = ctx.RequestServices.GetService<AudioDeviceSource>();
            var settings = ctx.RequestServices.GetService<SettingsContext>();
            if (source is null || settings is null) return Results.NotFound(new ErrorResponse("Audio device list is not available"));
            return Results.Ok(AudioDeviceChoices.Build(source.List(), settings.Current.Audio.OutputDevice,
                settings.LoadSaved().Audio.OutputDevice));
        });
        api.MapPost("/restart", (bool? force, SlotSet slots, HttpContext ctx) =>
        {
            if (ctx.RequestServices.GetService<SettingsContext>() is not { } s)
                return Results.NotFound(new ErrorResponse("Restart is not available"));
            var onAir = slots.All.Where(x => x.Status.OnProgram).Select(x => x.Status.Slot).ToArray();
            if (onAir.Length > 0 && force != true)
                return Results.Conflict(new ErrorResponse($"Slot {string.Join(", ", onAir)} is on program in vMix: restart requires force=true"));
            // Answer first: the restart stops this server.
            _ = Task.Delay(300).ContinueWith(_ => s.Restart(), TaskScheduler.Default);
            return Results.Accepted(value: new { Restarting = true });
        });

        // Presentations offered by the operator panel: GET /api/files.
        api.MapGet("/files", (ControlContext ctx) =>
        {
            if (!Directory.Exists(ctx.Inbox))
                return Results.Ok(Array.Empty<InboxFile>());
            var files = new DirectoryInfo(ctx.Inbox).EnumerateFiles("*", SearchOption.TopDirectoryOnly)
                .Where(f => f.Extension.ToLowerInvariant() is ".pptx" or ".ppsx" or ".ppt" && !f.Name.StartsWith("~$"))
                .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(f => new InboxFile(f.Name, f.FullName, f.Length, f.LastWriteTime))
                .ToArray();
            return Results.Ok(files);
        });

        var slot = api.MapGroup("/slots/{n:int}");
        slot.MapGet("/", (int n, SlotSet slots) => Run(n, slots, s => Task.FromResult(s.Status)));

        // On a slot that is on program in vMix, load/unload/reload need force=true: 409 otherwise.
        slot.MapPost("/load", (int n, LoadRequest body, bool? force, SlotSet slots) =>
            string.IsNullOrWhiteSpace(body.Path)
                ? Task.FromResult(Results.BadRequest(new ErrorResponse("'path' is required")))
                : Run(n, slots, s => s.LoadAsync(body.Path, body.StartSlide ?? 1, CancellationToken.None, force ?? body.Force ?? false)));
        slot.MapPost("/unload", (int n, bool? force, SlotSet slots) =>
            Run(n, slots, s => s.UnloadAsync(CancellationToken.None, force ?? false)));
        slot.MapPost("/reload", (int n, bool? force, SlotSet slots) =>
            Run(n, slots, s => s.ReloadAsync(CancellationToken.None, force ?? false)));

        slot.MapGet("/notes", (int n, SlotSet slots) =>
        {
            var s = slots.Find(n);
            return s is null
                ? Results.NotFound(new ErrorResponse($"Slot {n} does not exist"))
                : Results.Ok(new { Slot = n, Notes = s.Notes });
        });

        slot.MapGet("/thumb/{slide:int}.png", (int n, int slide, SlotSet slots) =>
        {
            var path = slots.Find(n)?.ThumbnailPath(slide);
            return path is null
                ? Results.NotFound(new ErrorResponse($"No thumbnail for slot {n} slide {slide} (yet)"))
                : Results.File(path, "image/png");
        });

        slot.MapGet("/live.mjpg", async (int n, HttpContext ctx, PreviewSet previews) =>
        {
            var preview = previews.Find(n);
            if (preview is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                await ctx.Response.WriteAsJsonAsync(new ErrorResponse($"No live preview for slot {n} (Preview.Live is off or the slot has no output)"));
                return;
            }
            await ServeMjpegAsync(ctx, preview);
        });

        MapCommand(slot, "next", s => s.NextAsync(CancellationToken.None));
        MapCommand(slot, "prev", s => s.PreviousAsync(CancellationToken.None));
        MapCommand(slot, "first", s => s.FirstAsync(CancellationToken.None));
        MapCommand(slot, "last", s => s.LastAsync(CancellationToken.None));
        MapCommand(slot, "black", s => Task.FromResult(s.SetBlack(true)));
        MapCommand(slot, "unblack", s => Task.FromResult(s.SetBlack(false)));
        MapCommand(slot, "freeze", s => Task.FromResult(s.SetFreeze(true)));
        MapCommand(slot, "unfreeze", s => Task.FromResult(s.SetFreeze(false)));

        slot.MapPost("/goto", (int n, GotoRequest body, SlotSet slots) =>
            Run(n, slots, s => s.GotoAsync(body.Slide, body.Click ?? 0, CancellationToken.None)));
        slot.MapGet("/goto", (int n, int slide, int? click, SlotSet slots) =>
            Run(n, slots, s => s.GotoAsync(slide, click ?? 0, CancellationToken.None)));
    }

    /// <summary>
    /// Long requests (/ws, MJPEG) end when the client leaves or the app stops. Without the second part an open
    /// panel tab held the shutdown for the host's 30 s timeout.
    /// </summary>
    private static CancellationTokenSource UntilStopping(HttpContext ctx) =>
        CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted,
            ctx.RequestServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);

    private const string MjpegBoundary = "karslidesframe";
    private static readonly byte[] MjpegDelimiter = Encoding.ASCII.GetBytes($"\r\n--{MjpegBoundary}\r\n");

    /// <summary>
    /// MJPEG stream of the live preview: plays in a plain &lt;img&gt;. The boundary is written right after
    /// each picture so browsers show it at once instead of waiting for the next one. While the picture does not
    /// change, the last one is repeated every 5 s to keep the connection alive.
    /// </summary>
    private static async Task ServeMjpegAsync(HttpContext ctx, LivePreview preview)
    {
        ctx.Response.ContentType = $"multipart/x-mixed-replace; boundary={MjpegBoundary}";
        ctx.Response.Headers.CacheControl = "no-cache, no-store";
        ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        using var ending = UntilStopping(ctx);
        var ct = ending.Token;
        var body = ctx.Response.Body;
        using var watch = preview.Watch();
        try
        {
            await body.WriteAsync(Encoding.ASCII.GetBytes($"--{MjpegBoundary}\r\n"), ct);
            long sequence = -1;
            while (!ct.IsCancellationRequested)
            {
                var image = await preview.WaitNewerAsync(sequence, TimeSpan.FromSeconds(5), ct) ?? preview.Latest;
                if (image is null) continue;
                var header = Encoding.ASCII.GetBytes($"Content-Type: image/jpeg\r\nContent-Length: {image.Jpeg.Length}\r\n\r\n");
                await body.WriteAsync(header, ct);
                await body.WriteAsync(image.Jpeg, ct);
                await body.WriteAsync(MjpegDelimiter, ct);
                await body.FlushAsync(ct);
                sequence = image.Sequence;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // the viewer closed the page
        }
    }

    private static void MapCommand(RouteGroupBuilder slot, string name, Func<SlotController, Task<SlotStatus>> command)
    {
        slot.MapMethods($"/{name}", ["GET", "POST"], (int n, SlotSet slots) => Run(n, slots, command));
    }

    private static async Task<IResult> Run(int n, SlotSet slots, Func<SlotController, Task<SlotStatus>> command)
    {
        var slot = slots.Find(n);
        if (slot is null) return Results.NotFound(new ErrorResponse($"Slot {n} does not exist"));
        try
        {
            return Results.Ok(await command(slot));
        }
        catch (SlotStateException ex)
        {
            return Results.Conflict(new ErrorResponse(ex.Message));
        }
        catch (Exception ex) when (ex is FileNotFoundException or ArgumentException)
        {
            return Results.BadRequest(new ErrorResponse(ex.Message));
        }
        catch (TimeoutException ex)
        {
            return Results.Json(new ErrorResponse(ex.Message), statusCode: StatusCodes.Status504GatewayTimeout);
        }
        catch (Exception ex)
        {
            return Results.Json(new ErrorResponse($"{ex.GetType().Name}: {ex.Message}"), statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}
