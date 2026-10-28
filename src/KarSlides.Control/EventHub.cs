using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using KarSlides.Core.Model;
using KarSlides.Core.Slots;

namespace KarSlides.Control;

/// <summary>One WebSocket message: {"type": "...", "time": "...", "data": {...}}.</summary>
public sealed record HubEvent(string Type, DateTimeOffset Time, object? Data);

/// <summary>
/// WebSocket events: slotState, position, tally, error, log, recovery. Slot changes are diffed
/// against the previous snapshot of the slot, so one change produces only the events it is about. Each client
/// has its own bounded queue: a slow client drops its oldest events instead of slowing others.
/// </summary>
public sealed class EventHub
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly object _sync = new();
    private readonly List<Channel<string>> _clients = [];
    private readonly Dictionary<int, SlotStatus> _last = new();

    public int ClientCount
    {
        get { lock (_sync) return _clients.Count; }
    }

    public void PublishSlot(SlotStatus s)
    {
        SlotStatus? prev;
        lock (_sync)
        {
            prev = _last.GetValueOrDefault(s.Slot);
            _last[s.Slot] = s;
        }

        if (prev is null || prev.State != s.State || prev.Output != s.Output || prev.File != s.File || prev.Thumbnails != s.Thumbnails)
            Publish("slotState", s);
        if (prev is null || prev.Slide != s.Slide || prev.Click != s.Click)
            Publish("position", new { s.Slot, s.Slide, s.SlideCount, s.Click, s.ClickCount });
        if (prev is null || prev.OnProgram != s.OnProgram || prev.OnPreview != s.OnPreview)
            Publish("tally", new { s.Slot, s.OnProgram, s.OnPreview });
        if (s.Error is not null && s.Error != prev?.Error)
            Publish("error", new { s.Slot, Message = s.Error });
    }

    public void PublishRecovery(RecoveryReport r) =>
        Publish("recovery", new { r.Reason, DurationMs = (int)r.Duration.TotalMilliseconds, r.Success });

    public void PublishLog(string level, string source, string message) =>
        Publish("log", new { Level = level, Source = source, Message = message });

    public void Publish(string type, object? data)
    {
        var text = JsonSerializer.Serialize(new HubEvent(type, DateTimeOffset.Now, data), Json);
        lock (_sync)
        {
            foreach (var c in _clients) c.Writer.TryWrite(text);
        }
    }

    /// <summary>Serves one WebSocket until it closes. The first message is the full state.</summary>
    public async Task ServeAsync(WebSocket socket, object initialState, CancellationToken ct)
    {
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
        channel.Writer.TryWrite(JsonSerializer.Serialize(new HubEvent("state", DateTimeOffset.Now, initialState), Json));
        lock (_sync) _clients.Add(channel);
        try
        {
            var receive = DrainIncomingAsync(socket, ct);
            await foreach (var text in channel.Reader.ReadAllAsync(ct).WithCancellation(ct))
            {
                if (receive.IsCompleted || socket.State != WebSocketState.Open) break;
                await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
        }
        finally
        {
            lock (_sync) _clients.Remove(channel);
        }
    }

    /// <summary>Clients only listen; reading detects the close.</summary>
    private static async Task DrainIncomingAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[1024];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var r = await socket.ReceiveAsync(buffer, ct);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
        }
    }
}
