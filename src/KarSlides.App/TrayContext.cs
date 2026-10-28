using System.Diagnostics;
using KarSlides.Core;
using KarSlides.Core.Model;
using KarSlides.Core.Slots;

namespace KarSlides.App;

/// <summary>
/// Tray icon and a minimal status window. The main UI is the web API; nothing here takes focus
/// unless the operator opens the window.
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly SlotRuntime _runtime;
    private readonly SlotSet _slots;
    private readonly AppStatus _status;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly System.Windows.Forms.Timer _timer;
    private StatusForm? _form;

    public TrayContext(SlotRuntime runtime, SlotSet slots, AppStatus status)
    {
        _runtime = runtime;
        _slots = slots;
        _status = status;
        _statusItem = new ToolStripMenuItem("Starting...") { Enabled = false };
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem($"KarSlides {Product.Display}") { Enabled = false });
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Status window", null, (_, _) => ShowStatus());
        menu.Items.Add("About KarSlides", null, (_, _) => ShowStatus());
        menu.Items.Add("Operator panel", null, (_, _) => Open(_runtime.PanelUrl));
        menu.Items.Add("Open API state in browser", null, (_, _) => Open(_runtime.ApiUrl));
        menu.Items.Add("Open logs folder", null, (_, _) => Open(Program.LogDirectory));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _icon = new NotifyIcon
        {
            Icon = AppIcon,
            Text = $"KarSlides {Product.Version} – starting",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => ShowStatus();

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => Refresh();
        _ = StartAsync();
        WatchExitRequest();
    }

    private static ProductInfo Product => ProductInfo.Current;

    /// <summary>The exe icon (app.ico from logo.png); the generic one if it cannot be read.</summary>
    internal static Icon AppIcon { get; } = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;

    /// <summary>
    /// Graceful stop from outside (installer, scripts, tests): set the named event and the app exits exactly as
    /// through the tray menu – shows closed, PowerPoint quit, settings restored.
    /// </summary>
    public const string ExitEventName = @"Local\KarSlides.App.Exit";

    private readonly EventWaitHandle _exitRequest = new(false, EventResetMode.AutoReset, ExitEventName);

    private void WatchExitRequest()
    {
        var ui = SynchronizationContext.Current!;
        ThreadPool.RegisterWaitForSingleObject(_exitRequest, (_, timedOut) =>
        {
            if (!timedOut) ui.Post(_ => ExitThread(), null);
        }, null, Timeout.Infinite, executeOnlyOnce: true);
    }

    private async Task StartAsync()
    {
        await _runtime.Started;
        Refresh();
        _timer.Start();
        var problems = _status.Problems;
        if (problems.Count > 0)
            _icon.ShowBalloonTip(10_000, "KarSlides: problems at start-up", string.Join("\n", problems), ToolTipIcon.Error);
    }

    private void Refresh()
    {
        var line = StatusLine();
        _statusItem.Text = line;
        _icon.Text = Truncate($"KarSlides {Product.Version} – {line}", 63);
        _form?.UpdateText(Details());
    }

    private string StatusLine()
    {
        if (_status.Problems.Count > 0 && !_slots.All.Any()) return "not working, see status";
        var s = _slots.All.FirstOrDefault()?.Status;
        if (s is null) return _status.Started ? "no slot" : "starting...";
        return s.State switch
        {
            SlotState.Ready => $"slot 1: {s.Slide}/{s.SlideCount} {s.Output.ToString().ToLowerInvariant()}",
            SlotState.Error => "slot 1: ERROR",
            _ => $"slot 1: {s.State.ToString().ToLowerInvariant()}",
        };
    }

    private string Details()
    {
        var lines = new List<string>
        {
            $"KarSlides {Product.Display}",
            Product.Copyright,
            $"{Product.NdiNotice}, {Product.NdiUrl}",
            "",
            $"Panel: {_runtime.PanelUrl}",
            $"API: {_runtime.ApiUrl}",
            $"NDI® source: {_runtime.NdiName ?? "-"}",
            "",
        };
        foreach (var s in _slots.All.Select(x => x.Status))
        {
            lines.Add($"Slot {s.Slot}: {s.State}");
            if (s.File is not null) lines.Add($"  File: {s.File}");
            if (s.SlideCount > 0) lines.Add($"  Slide {s.Slide}/{s.SlideCount}, click {s.Click}/{s.ClickCount}");
            lines.Add($"  Output: {s.Output}");
            if (s.Error is not null) lines.Add($"  Error: {s.Error}");
        }
        var problems = _status.Problems;
        if (problems.Count > 0)
        {
            lines.Add("");
            lines.Add("Problems:");
            lines.AddRange(problems.Select(p => "  " + p));
        }
        lines.Add("");
        lines.Add($"Logs: {Program.LogDirectory}");
        lines.Add($"Settings: {Program.UserSettingsFile}");
        return string.Join(Environment.NewLine, lines);
    }

    private void ShowStatus()
    {
        if (_form is null || _form.IsDisposed)
        {
            _form = new StatusForm();
            _form.FormClosed += (_, _) => _form = null;
        }
        _form.UpdateText(Details());
        _form.Show();
        _form.Activate(); // the operator asked for it
    }

    private static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Serilog.Log.Warning(ex, "Cannot open {Target}", target); }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _exitRequest.Dispose();
            _icon.Visible = false;
            _icon.Dispose();
            _form?.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed class StatusForm : Form
    {
        private readonly TextBox _text;

        public StatusForm()
        {
            Text = $"KarSlides {Product.Display}";
            Icon = AppIcon;
            Width = 640;
            Height = 360;
            StartPosition = FormStartPosition.CenterScreen;
            _text = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                Dock = DockStyle.Fill,
                Font = new Font(FontFamily.GenericMonospace, 10),
                ScrollBars = ScrollBars.Vertical,
            };
            Controls.Add(_text);
        }

        public void UpdateText(string text)
        {
            if (_text.Text != text) _text.Text = text;
        }
    }
}
