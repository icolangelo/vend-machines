using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using VendingMachines.Api.Services;

namespace VendingMachines.Api.Middlewares;

public class WebSocketMiddleware
{
    private readonly RequestDelegate _next;
    private readonly TelemetryManager _telemetry;
    private static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(30);
    private const int MaxRetries = 5;

    public WebSocketMiddleware(RequestDelegate next, TelemetryManager telemetry)
    {
        _next = next;
        _telemetry = telemetry;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.WebSockets.IsWebSocketRequest)
        {
            if (context.Request.Path == "/sitehandler.ashx")
            {
                using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                await HandleSiteWebSocket(webSocket);
                return;
            }
            else if (context.Request.Path.StartsWithSegments("/telemetria"))
            {
                using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                await HandleEspWebSocket(webSocket);
                return;
            }
        }
        await _next(context);
    }

    private async Task HandleSiteWebSocket(WebSocket socket)
    {
        string clientId = Guid.NewGuid().ToString();
        _telemetry.SiteClients.TryAdd(clientId, socket);

        var buffer = new byte[4096];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    string msg = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    try
                    {
                        using var doc = JsonDocument.Parse(msg);
                        var root = doc.RootElement;
                        
                        string type = root.GetProperty("type").GetString() ?? "";
                        string target = root.GetProperty("target").GetString() ?? "";
                        
                        if (type == "subscribe" && !string.IsNullOrEmpty(target))
                        {
                            _telemetry.SiteRegistrations[clientId] = target;
                            await _telemetry.SendToSingleSite(socket, new { type = "ack", target, data = "Inscrito com sucesso" });
                        }
                        else if (type == "msg" && !string.IsNullOrEmpty(target))
                        {
                            string data = root.GetProperty("data").GetString() ?? "";
                            
                            // Mocking config update for now
                            if (data == "ATUALIZAR_CONFIGURACAO")
                            {
                                await _telemetry.SendToSingleSite(socket, new { type = "ack", target, data = "Comando de configuracao enviado a fila!" });
                            }
                            else
                            {
                                _telemetry.EnqueueRawMessageToEsp(target, data);
                                await _telemetry.SendToSingleSite(socket, new { type = "ack", target, data = "Comando comum enviado" });
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Erro JSON site: {ex.Message}");
                    }
                }
                else if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
            }
        }
        finally
        {
            _telemetry.SiteClients.TryRemove(clientId, out _);
            _telemetry.SiteRegistrations.TryRemove(clientId, out _);
            if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Fechado", CancellationToken.None);
            }
        }
    }

    private async Task HandleEspWebSocket(WebSocket socket)
    {
        string clientId = Guid.NewGuid().ToString();
        var state = new ClientState();
        
        if (!_telemetry.EspStates.TryAdd(clientId, state) || !_telemetry.EspClients.TryAdd(clientId, socket))
            return;

        _ = ProcessEspQueueAsync(clientId, state, socket);

        var buffer = new byte[8192];
        try
        {
            while (socket.State == WebSocketState.Open && !state.Cts.Token.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), state.Cts.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    await ProcessEspMessage(clientId, state, socket, buffer, result.Count);
                }
            }
        }
        catch(OperationCanceledException) {}
        finally
        {
            state.Cts.Cancel();
            state.ClearQueue();
            _telemetry.EspClients.TryRemove(clientId, out _);
            _telemetry.EspStates.TryRemove(clientId, out _);
            // Notifica o frontend sobre a desconexão antes de remover o identificador
            if (_telemetry.ClientIdentifiers.TryRemove(clientId, out var disconnectedSerial) && !string.IsNullOrEmpty(disconnectedSerial))
            {
                await _telemetry.BroadcastToAllSites(new { type = "device_disconnected", serial = disconnectedSerial });
            }
        }
    }

    private async Task ProcessEspQueueAsync(string clientId, ClientState state, WebSocket socket)
    {
        while (!state.Cts.Token.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            try
            {
                await Task.Delay(100, state.Cts.Token);
                if (state.CurrentSendingId != 0)
                {
                    if (DateTime.UtcNow - state.LastSendTime > AckTimeout)
                    {
                        if (state.RetryCount < MaxRetries)
                        {
                            state.RetryCount++;
                            if (state.Queue.TryPeek(out var last))
                            {
                                state.LastSendTime = DateTime.UtcNow;
                                var bytes = Encoding.UTF8.GetBytes(last.json);
                                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, state.Cts.Token);
                            }
                        }
                        else
                        {
                            state.ClearQueue();
                        }
                    }
                }
                else if (state.Queue.TryPeek(out var next))
                {
                    state.CurrentSendingId = next.id;
                    state.RetryCount = 0;
                    state.LastSendTime = DateTime.UtcNow;
                    var bytes = Encoding.UTF8.GetBytes(next.json);
                    await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, state.Cts.Token);
                }
            }
            catch (Exception) { break; }
        }
    }

    private async Task ProcessEspMessage(string clientId, ClientState state, WebSocket socket, byte[] buffer, int count)
    {
        try
        {
            string msg = Encoding.UTF8.GetString(buffer, 0, count);
            using var doc = JsonDocument.Parse(msg);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp) || !root.TryGetProperty("id", out var idProp)) return;
            
            string type = typeProp.GetString() ?? "";
            int receivedId = idProp.GetInt32();
            
            _telemetry.ClientIdentifiers.TryGetValue(clientId, out string? deviceSerial);
            deviceSerial ??= "unknown";

            switch (type)
            {
                case "start":
                    if (root.TryGetProperty("target", out var targetProp))
                    {
                        deviceSerial = targetProp.GetString() ?? "";
                        _telemetry.ClientIdentifiers[clientId] = deviceSerial;
                        // Notifica todos os clientes site (frontend) que uma máquina IoT conectou
                        await _telemetry.BroadcastToAllSites(new { type = "device_connected", serial = deviceSerial });
                    }
                    var ackStartBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { id = receivedId, type = "ack", data = "ok" }));
                    await socket.SendAsync(new ArraySegment<byte>(ackStartBytes), WebSocketMessageType.Text, true, CancellationToken.None);
                    break;

                case "ack":
                    if (receivedId == state.CurrentSendingId)
                    {
                        state.Queue.TryDequeue(out _);
                        state.CurrentSendingId = 0;
                        state.RetryCount = 0;
                    }
                    break;
                    
                case "ping":
                    var pongBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { id = receivedId, type = "pong", serverTime = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"), status = "ok" }));
                    await socket.SendAsync(new ArraySegment<byte>(pongBytes), WebSocketMessageType.Text, true, CancellationToken.None);
                    break;
                    
                case "msg":
                    if (receivedId != state.LastReceivedIdFromEsp)
                    {
                        state.LastReceivedIdFromEsp = receivedId;
                        var ackBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { id = receivedId, type = "ack", data = "ok" }));
                        await socket.SendAsync(new ArraySegment<byte>(ackBytes), WebSocketMessageType.Text, true, CancellationToken.None);
                        
                        string dataPayload = root.GetProperty("data").GetString() ?? "";
                        await _telemetry.SendToSitesByDevice(deviceSerial, dataPayload);
                    }
                    break;
            }
        }
        catch { }
    }
}
