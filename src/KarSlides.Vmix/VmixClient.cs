using System.Net.Sockets;
using System.Text;
using KarSlides.Core.Configuration;
using KarSlides.Core.Model;
using Microsoft.Extensions.Logging;

namespace KarSlides.Vmix;

/// <summary>What the API shows about vMix.</summary>
public sealed record VmixStatus(bool Connected, string? Host, int? HttpPort, string? Version, string? Error,
    IReadOnlyDictionary<int, int> SlotInputs);

/// <summary>
/// vMix integration: reads the input list over the XML API to find the input of each slot's NDI
/// source, subscribes to tally over TCP and pushes OnProgram/OnPreview to the slots. Reconnects forever;
/// without vMix everything works, just without tally (slots get <see cref="Tally.None"/>).
/// </summary>
/// <param name="discoverPorts">Ports vMix listens on (for HttpPort = 0); defaults to <see cref="VmixDiscovery"/>.</param>
public sealed class VmixClient(VmixOptions options, HttpClient http, ILogger<VmixClient> log,
    Func<IReadOnlyList<int>>? discoverPorts = null)
{
    private readonly Func<IReadOnlyList<int>> _discoverPorts =
        discoverPorts ?? (() => VmixDiscovery.ListeningHttpCandidates(options.TcpPort));
    private int? _port;

    private readonly object _sync = new();
    private readonly Dictionary<int, string> _slotNdiNames = new();
    private readonly Dictionary<int, Action<Tally>> _slotSinks = new();
    private Dictionary<int, int> _slotInputs = new();
    private string? _host, _version, _error, _lastTally;
    private bool _connected;

    /// <summary>Registers a slot: its NDI source name (without machine) and where its tally goes.</summary>
    public void AddSlot(int slot, string ndiName, Action<Tally> setTally)
    {
        lock (_sync)
        {
            _slotNdiNames[slot] = ndiName;
            _slotSinks[slot] = setTally;
        }
    }

    public VmixStatus Status
    {
        get
        {
            lock (_sync) return new VmixStatus(_connected, _host, _port, _version, _error, new Dictionary<int, int>(_slotInputs));
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var refresh = TimeSpan.FromSeconds(options.InputRefreshSeconds);
        var reportedError = (string?)null;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RefreshInputsAsync(ct);
                await RunTallyAsync(refresh, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                var message = ex is HttpRequestException or SocketException or IOException or TaskCanceledException
                    ? $"vMix not reachable: {ex.Message}"
                    : ex.Message;
                SetDisconnected(message);
                if (message != reportedError)
                {
                    log.LogWarning("vMix: {Message}. Working without tally, retrying", message);
                    reportedError = message;
                }
            }

            try { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Finds vMix and maps slots to inputs. Host "auto": [::1], then 127.0.0.1. HttpPort 0: the ports the vMix
    /// process listens on are probed for the vMix XML answer (the Web Controller port can be anything).
    /// The working host and port are kept until they stop answering.
    /// </summary>
    public async Task RefreshInputsAsync(CancellationToken ct)
    {
        string? knownHost;
        int? knownPort;
        lock (_sync) (knownHost, knownPort) = (_host, _port);

        var hosts = knownHost is not null ? [knownHost]
            : options.Host.Equals("auto", StringComparison.OrdinalIgnoreCase) ? ["[::1]", "127.0.0.1"] : new[] { options.Host };
        IReadOnlyList<int> ports = knownPort is { } kp ? [kp]
            : options.HttpPort > 0 ? [options.HttpPort]
            : IsLocal(hosts[0]) ? _discoverPorts() : [8088];
        if (ports.Count == 0)
            throw new IOException("vMix is not running (no vMix process listening)");

        Exception? last = null;
        foreach (var port in ports)
        foreach (var host in hosts)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(ports.Count > 1 ? 1 : 3));
                var xml = await http.GetStringAsync($"http://{host}:{port}/api/", timeout.Token);
                var state = VmixProtocol.ParseState(xml);
                ApplyInputs(host, port, state);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                last = ex is FormatException
                    ? new FormatException($"something other than vMix answers on {host}:{port} " +
                                          "(set Vmix.HttpPort to the vMix Web Controller port or 0 for automatic)", ex)
                    : ex;
            }
        }
        lock (_sync) (_host, _port) = (null, null); // probe again next time
        throw ports.Count > 1 && last is not FormatException
            ? new IOException($"no vMix Web Controller found on ports {string.Join(", ", ports)}: is Web Controller enabled in vMix settings?", last)
            : last!;
    }

    private static bool IsLocal(string host) =>
        host is "auto" or "[::1]" or "::1" or "127.0.0.1" || host.Equals("localhost", StringComparison.OrdinalIgnoreCase);

    private void ApplyInputs(string host, int port, VmixState state)
    {
        Dictionary<int, int> map;
        string? tally;
        lock (_sync)
        {
            map = _slotNdiNames
                .Select(kv => (Slot: kv.Key, Input: VmixProtocol.FindNdiInput(state, kv.Value)))
                .Where(x => x.Input is not null)
                .ToDictionary(x => x.Slot, x => x.Input!.Number);
            var changed = _host != host || _port != port || !map.OrderBy(k => k.Key).SequenceEqual(_slotInputs.OrderBy(k => k.Key));
            if (changed)
                log.LogInformation("vMix {Version} at {Host}:{Port}{Auto}; slot inputs: {Map}", state.Version, host, port,
                    options.HttpPort > 0 ? "" : " (found automatically)",
                    map.Count == 0 ? "none (add the KarSlides NDI source as an input)" : string.Join(", ", map.Select(m => $"slot {m.Key} -> input {m.Value}")));
            _host = host;
            _port = port;
            _version = state.Version;
            _slotInputs = map;
            tally = _lastTally;
        }
        if (tally is not null) PushTally(tally);
    }

    private async Task RunTallyAsync(TimeSpan refresh, CancellationToken ct)
    {
        string host;
        lock (_sync) host = _host!;
        using var tcp = new TcpClient();
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await tcp.ConnectAsync(host.Trim('[', ']'), options.TcpPort, timeout.Token);
        }
        await using var stream = tcp.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var send = Encoding.UTF8.GetBytes("SUBSCRIBE TALLY\r\nTALLY\r\n");
        await stream.WriteAsync(send, ct);

        lock (_sync)
        {
            _connected = true;
            _error = null;
        }
        log.LogInformation("vMix tally subscribed on {Host}:{Port}", host, options.TcpPort);

        var nextRefresh = DateTime.UtcNow + refresh;
        var readTask = reader.ReadLineAsync(ct).AsTask();
        while (!ct.IsCancellationRequested)
        {
            var delay = nextRefresh - DateTime.UtcNow;
            if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
            var done = await Task.WhenAny(readTask, Task.Delay(delay, ct));
            if (done == readTask)
            {
                var line = await readTask ?? throw new IOException("vMix closed the TCP connection");
                var tally = VmixProtocol.ParseTally(line);
                if (tally is not null)
                {
                    lock (_sync) _lastTally = tally;
                    PushTally(tally);
                }
                readTask = reader.ReadLineAsync(ct).AsTask();
            }
            else
            {
                await RefreshInputsAsync(ct); // inputs may have been added or reordered
                nextRefresh = DateTime.UtcNow + refresh;
            }
        }
    }

    private void PushTally(string tally)
    {
        List<(Action<Tally> Sink, Tally Tally)> updates;
        lock (_sync)
        {
            updates = _slotSinks.Select(kv => (kv.Value,
                _slotInputs.TryGetValue(kv.Key, out var input) ? VmixProtocol.TallyOf(tally, input) : Tally.None)).ToList();
        }
        foreach (var (sink, t) in updates) sink(t);
    }

    private void SetDisconnected(string error)
    {
        List<Action<Tally>> sinks;
        lock (_sync)
        {
            _connected = false;
            _error = error;
            _lastTally = null;
            sinks = _slotSinks.Values.ToList();
        }
        foreach (var sink in sinks) sink(Tally.None); // unknown = not on air: no force needed without vMix
    }
}
