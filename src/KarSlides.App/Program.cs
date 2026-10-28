using System.Diagnostics;
using KarSlides.Control;
using KarSlides.Core.Clicker;
using KarSlides.Core.Configuration;
using KarSlides.Core.Preview;
using KarSlides.Core.Slots;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace KarSlides.App;

internal static class Program
{
    private const string MutexName = @"Local\KarSlides.App";

    public static string LogDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KarSlides", "logs");

    public static string UserSettingsFile { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KarSlides", "settings.json");

    public static string BaseSettingsFile { get; } = Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    [STAThread]
    private static int Main(string[] args)
    {
        WaitForPreviousInstance(args);
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var first);

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Configuration.Sources.Clear();
        builder.Configuration
            .AddJsonFile(BaseSettingsFile, optional: true)
            .AddJsonFile(UserSettingsFile, optional: true);
        var options = new KarSlidesOptions();
        BindOptions(builder.Configuration, options);

        var hub = new EventHub();
        Log.Logger = CreateLogger(options.Logging, hub);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception");

        if (!first)
        {
            // No MessageBox: a second start (autostart + manual) must not steal focus from vMix.
            Log.Warning("KarSlides is already running, exiting");
            Log.CloseAndFlush();
            return 1;
        }

        Log.Information("KarSlides starting, version {Version}, {Copyright}, settings: {Base} + {User}",
            KarSlides.Core.ProductInfo.Current.Display, KarSlides.Core.ProductInfo.Current.Copyright, BaseSettingsFile, UserSettingsFile);

        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace); // Serilog decides (Logging.Level); MEL would cut Debug off
        builder.Logging.AddProvider(new SerilogLoggerProvider(Log.Logger));
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(hub);
        builder.Services.AddSingleton<AppStatus>();
        builder.Services.AddSingleton<SlotSet>();
        builder.Services.AddSingleton<PreviewSet>();
        builder.Services.AddSingleton(sp => new ClickerController(options.Clicker, sp.GetRequiredService<SlotSet>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<ClickerController>()));
        builder.Services.AddSingleton(new ControlContext(options.Paths.InboxExpanded, Path.Combine(AppContext.BaseDirectory, "web")));
        builder.Services.AddSingleton(new SettingsContext(UserSettingsFile, options,
            LoadSaved: () => LoadOptions(BaseSettingsFile, UserSettingsFile),
            LoadBase: () => LoadOptions(BaseSettingsFile),
            Restart: Restart));
        builder.Services.AddSingleton(new MonitorSource(() => KarSlides.Capture.DisplayMonitors.Enumerate()));
        builder.Services.AddSingleton(new AudioDeviceSource(() => new KarSlides.Audio.WindowsAudioRouter().Devices()));
        builder.Services.AddSingleton<SlotRuntime>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<SlotRuntime>());
        ControlServer.Configure(builder, options.Control);

        var app = builder.Build();
        ControlServer.MapApi(app, options.Control);
        var status = app.Services.GetRequiredService<AppStatus>();

        try
        {
            try
            {
                app.StartAsync().GetAwaiter().GetResult();
                Log.Information("API listening on {Bind}:{Port}", options.Control.Bind, options.Control.Port);
            }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException)
            {
                status.AddProblem($"API: port {options.Control.Port} is not available ({ex.Message})");
                Log.Error(ex, "API could not start");
            }

            var clicker = app.Services.GetRequiredService<ClickerController>();
            clicker.EnabledChanged += on => hub.Publish("clicker", new { Enabled = on });
            using var hook = new KeyboardHook(clicker, app.Services.GetRequiredService<ILoggerFactory>().CreateLogger<KeyboardHook>());

            ApplicationConfiguration.Initialize();
            using var tray = new TrayContext(app.Services.GetRequiredService<SlotRuntime>(),
                app.Services.GetRequiredService<SlotSet>(), status);
            Application.Run(tray);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "KarSlides crashed");
            return 2;
        }
        finally
        {
            try { app.StopAsync().GetAwaiter().GetResult(); }
            catch (Exception ex) { Log.Error(ex, "Stop failed"); }
            ((IDisposable)app).Dispose();
            Log.Information("KarSlides stopped");
            Log.CloseAndFlush();
        }
    }

    private const string WaitPidArg = "--wait-pid";

    /// <summary>
    /// Restart from the settings page: a new instance waits for this one to exit (mutex, port 9180,
    /// PowerPoint), then this one stops exactly as through the tray menu.
    /// </summary>
    private static void Restart()
    {
        Log.Information("Restart requested from the settings page");
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"{WaitPidArg} {Environment.ProcessId}")
            {
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            });
            using var exit = EventWaitHandle.OpenExisting(TrayContext.ExitEventName);
            exit.Set();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Restart failed");
        }
    }

    private static void WaitForPreviousInstance(string[] args)
    {
        var i = Array.IndexOf(args, WaitPidArg);
        if (i < 0 || i + 1 >= args.Length || !int.TryParse(args[i + 1], out var pid)) return;
        try
        {
            using var previous = Process.GetProcessById(pid);
            previous.WaitForExit(TimeSpan.FromSeconds(90));
        }
        catch (ArgumentException)
        {
            // already gone
        }
    }

    private static KarSlidesOptions LoadOptions(params string[] files)
    {
        var config = new ConfigurationBuilder();
        foreach (var f in files) config.AddJsonFile(f, optional: true, reloadOnChange: false);
        var options = new KarSlidesOptions();
        BindOptions(config.Build(), options);
        return options;
    }

    /// <summary>
    /// Binds settings. Lists configured in a file replace the defaults: the binder appends to a non-empty list,
    /// which turned ["virtual"] + ["MTT1337"] into ["virtual", "MTT1337"] and silently ignored the setting.
    /// </summary>
    internal static void BindOptions(IConfiguration config, KarSlidesOptions options)
    {
        config.Bind(options);
        List<string> Replace(string path, List<string> current)
        {
            var section = config.GetSection(path);
            return section.Exists() ? section.Get<List<string>>() ?? [] : current;
        }
        options.Slots.Monitors = Replace("Slots:Monitors", new KarSlidesOptions().Slots.Monitors);
        var d = new ClickerOptions();
        options.Clicker.Next = Replace("Clicker:Next", d.Next);
        options.Clicker.Prev = Replace("Clicker:Prev", d.Prev);
        options.Clicker.Black = Replace("Clicker:Black", d.Black);
        options.Clicker.Swallow = Replace("Clicker:Swallow", d.Swallow);
    }

    private static Serilog.ILogger CreateLogger(LoggingOptions logging, EventHub hub)
    {
        var level = Enum.TryParse<LogEventLevel>(logging.Level, ignoreCase: true, out var l) ? l : LogEventLevel.Information;
        return new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .Enrich.With(new ThreadIdEnricher())
            .WriteTo.File(Path.Combine(LogDirectory, "karslides-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: logging.RetainedDays,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [T{ThreadId}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .WriteTo.Sink(new HubLogSink(hub), LogEventLevel.Warning)
            .CreateLogger();
    }

    /// <summary>Warnings and errors go to WebSocket clients as "log" events.</summary>
    private sealed class HubLogSink(EventHub hub) : Serilog.Core.ILogEventSink
    {
        public void Emit(LogEvent e)
        {
            var source = e.Properties.TryGetValue("SourceContext", out var sc) ? sc.ToString().Trim('"') : "";
            hub.PublishLog(e.Level.ToString(), source, e.RenderMessage());
        }
    }

    private sealed class ThreadIdEnricher : Serilog.Core.ILogEventEnricher
    {
        public void Enrich(LogEvent e, Serilog.Core.ILogEventPropertyFactory f) =>
            e.AddPropertyIfAbsent(f.CreateProperty("ThreadId", Environment.CurrentManagedThreadId));
    }
}
