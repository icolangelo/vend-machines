using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace VendingMachines.Api.Services;

public class ClientState
{
    public int CurrentSendingId { get; set; } = 0;
    public int RetryCount { get; set; } = 0;
    public int ReportRetryCount { get; set; } = 0;
    public DateTime LastSendTime { get; set; }
    public ConcurrentQueue<(int id, string json)> Queue { get; set; } = new();
    public CancellationTokenSource Cts { get; set; } = new();
    public int LastSentId = 0;
    public int LastReceivedIdFromEsp { get; set; } = -1;

    public void ClearQueue()
    {
        while (Queue.TryDequeue(out _)) { }
        CurrentSendingId = 0;
        RetryCount = 0;
    }
}

public class TelemetryManager
{
    // ESP32 Clients
    public ConcurrentDictionary<string, WebSocket> EspClients { get; } = new();
    public ConcurrentDictionary<string, ClientState> EspStates { get; } = new();
    public ConcurrentDictionary<string, string> ClientIdentifiers { get; } = new(); // clientId -> deviceSerial

    // Site (React Frontend) Clients
    public ConcurrentDictionary<string, WebSocket> SiteClients { get; } = new();
    public ConcurrentDictionary<string, string> SiteRegistrations { get; } = new(); // siteClientId -> deviceSerial to listen

    public void EnqueueMessageToEsp(string deviceSerial, string action, string data = "")
    {
        var connection = ClientIdentifiers.FirstOrDefault(x => x.Value == deviceSerial);
        if (connection.Key != null && EspStates.TryGetValue(connection.Key, out var state))
        {
            int messageId = Interlocked.Increment(ref state.LastSentId);
            var payload = new { id = messageId, type = "cmd", action = action, data = data };
            state.Queue.Enqueue((messageId, JsonSerializer.Serialize(payload)));
        }
    }

    public void EnqueueRawMessageToEsp(string deviceSerial, string data)
    {
        var connection = ClientIdentifiers.FirstOrDefault(x => x.Value == deviceSerial);
        if (connection.Key != null && EspStates.TryGetValue(connection.Key, out var state))
        {
            int messageId = Interlocked.Increment(ref state.LastSentId);
            var payload = new { id = messageId, type = "msg", data = data };
            state.Queue.Enqueue((messageId, JsonSerializer.Serialize(payload)));
        }
    }

    public async Task SendToSitesByDevice(string deviceSerial, string jsonToSend)
    {
        var targetBytes = Encoding.UTF8.GetBytes(jsonToSend);
        var segment = new ArraySegment<byte>(targetBytes);

        foreach (var kv in SiteClients.ToArray())
        {
            if (SiteRegistrations.TryGetValue(kv.Key, out var registeredSerial) && registeredSerial == deviceSerial)
            {
                if (kv.Value.State == WebSocketState.Open)
                {
                    try
                    {
                        await kv.Value.SendAsync(segment, WebSocketMessageType.Text, true, CancellationToken.None);
                    }
                    catch { /* Site might have disconnected */ }
                }
            }
        }
    }

    public async Task SendToSingleSite(WebSocket socket, object obj)
    {
        if (socket.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(obj));
        await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
    }
}
