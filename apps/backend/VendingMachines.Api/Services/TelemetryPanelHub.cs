using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace VendingMachines.Api.Services;

public sealed class TelemetryPanelHub
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<Guid, PanelConnection> _clients = new();
    private readonly ILogger<TelemetryPanelHub> _logger;

    public TelemetryPanelHub(ILogger<TelemetryPanelHub> logger) => _logger = logger;

    public async Task HandleAsync(WebSocket socket, WebSocketTicket ticket, CancellationToken requestAborted)
    {
        var client = new PanelConnection(ticket.UserId, ticket.CompanyId, socket);
        _clients[client.Id] = client;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, client.Cts.Token);
        var writer = WriteLoopAsync(client, linked.Token);

        try
        {
            await client.Outbound.Writer.WriteAsync(JsonSerializer.Serialize(new
            {
                type = "connected",
                serverTime = DateTime.UtcNow
            }, JsonOptions), linked.Token);

            while (socket.State == WebSocketState.Open && !linked.IsCancellationRequested)
            {
                var message = await ReadTextAsync(socket, linked.Token);
                if (message == null) break;

                try
                {
                    using var document = JsonDocument.Parse(message);
                    var root = document.RootElement;
                    var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
                    if (type == "subscribe" && root.TryGetProperty("machineIds", out var machines) && machines.ValueKind == JsonValueKind.Array)
                    {
                        client.Subscriptions.Clear();
                        foreach (var machine in machines.EnumerateArray())
                        {
                            var id = machine.GetString();
                            if (!string.IsNullOrWhiteSpace(id)) client.Subscriptions[id] = 0;
                        }
                    }
                    else if (type == "unsubscribe")
                    {
                        client.Subscriptions.Clear();
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogDebug(ex, "Mensagem inválida recebida do painel de telemetria.");
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _clients.TryRemove(client.Id, out _);
            client.Cts.Cancel();
            client.Outbound.Writer.TryComplete();
            try { await writer; } catch { }
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Fechado", CancellationToken.None); } catch { }
            }
        }
    }

    public Task BroadcastCompanyAsync(Guid companyId, object payload) =>
        BroadcastAsync(companyId, null, payload);

    public Task BroadcastMachineAsync(Guid companyId, string machineId, object payload) =>
        BroadcastAsync(companyId, machineId, payload);

    private Task BroadcastAsync(Guid companyId, string? subscribedMachineId, object payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        foreach (var client in _clients.Values)
        {
            if (client.CompanyId != companyId) continue;
            if (subscribedMachineId != null && !client.Subscriptions.ContainsKey(subscribedMachineId)) continue;
            client.Outbound.Writer.TryWrite(json);
        }
        return Task.CompletedTask;
    }

    private static async Task WriteLoopAsync(PanelConnection client, CancellationToken cancellationToken)
    {
        await foreach (var json in client.Outbound.Reader.ReadAllAsync(cancellationToken))
        {
            if (client.Socket.State != WebSocketState.Open) break;
            var bytes = Encoding.UTF8.GetBytes(json);
            await client.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
        }
    }

    private static async Task<string?> ReadTextAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[4096];
        do
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text) continue;
            await stream.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken);
            if (stream.Length > 64 * 1024) throw new WebSocketException("Payload do painel excedeu 64 KB.");
            if (result.EndOfMessage) break;
        } while (true);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private sealed class PanelConnection
    {
        public PanelConnection(Guid userId, Guid companyId, WebSocket socket)
        {
            UserId = userId;
            CompanyId = companyId;
            Socket = socket;
        }

        public Guid Id { get; } = Guid.NewGuid();
        public Guid UserId { get; }
        public Guid CompanyId { get; }
        public WebSocket Socket { get; }
        public CancellationTokenSource Cts { get; } = new();
        public ConcurrentDictionary<string, byte> Subscriptions { get; } = new();
        public Channel<string> Outbound { get; } = Channel.CreateBounded<string>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
    }
}
