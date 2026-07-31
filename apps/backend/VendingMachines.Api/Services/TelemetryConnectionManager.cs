using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;

namespace VendingMachines.Api.Services;

public sealed record InboundDeviceMessage(
    string MachineId,
    Guid CompanyId,
    string Serial,
    string ConnectionId,
    int MessageId,
    string DataJson,
    DateTime ReceivedAt);

public sealed record DeviceCommandResult(bool Success, int MessageId, int Attempts, string? Error = null);

public sealed record DeviceStatusResult(
    bool Success,
    int MessageId,
    string? Status,
    string? DataJson,
    DateTime? ResponseAt,
    string? Error = null);

public sealed class TelemetryConnectionManager : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, DeviceConnection> _devices = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _machineMessageGates = new();
    private readonly Channel<InboundDeviceMessage> _inbound = Channel.CreateBounded<InboundDeviceMessage>(
        new BoundedChannelOptions(5000) { FullMode = BoundedChannelFullMode.Wait, SingleReader = false, SingleWriter = false });
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TelemetryPanelHub _panelHub;
    private readonly ILogger<TelemetryConnectionManager> _logger;

    public TelemetryConnectionManager(
        IServiceScopeFactory scopeFactory,
        TelemetryPanelHub panelHub,
        ILogger<TelemetryConnectionManager> logger)
    {
        _scopeFactory = scopeFactory;
        _panelHub = panelHub;
        _logger = logger;
    }

    public bool IsOnline(string serial) =>
        _devices.TryGetValue(NormalizeSerial(serial), out var connection) && connection.Socket.State == WebSocketState.Open;

    public async Task HandleDeviceAsync(WebSocket socket, CancellationToken requestAborted)
    {
        DeviceConnection? connection = null;
        using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            var firstMessage = await ReadTextAsync(socket, handshakeTimeout.Token);
            if (firstMessage == null) return;
            using var firstDocument = JsonDocument.Parse(firstMessage);
            var first = firstDocument.RootElement;
            if (!first.TryGetProperty("type", out var type) || type.GetString() != "start" ||
                !first.TryGetProperty("target", out var target) || string.IsNullOrWhiteSpace(target.GetString()))
            {
                await ClosePolicyViolationAsync(socket, "A primeira mensagem deve ser start com target.");
                return;
            }

            var serial = NormalizeSerial(target.GetString()!);
            var machine = await FindMachineAsync(serial, requestAborted);
            if (machine == null || !machine.CompanyId.HasValue)
            {
                await ClosePolicyViolationAsync(socket, "Máquina não cadastrada.");
                return;
            }

            connection = new DeviceConnection(machine.Id, machine.CompanyId.Value, serial, socket);
            if (_devices.TryGetValue(serial, out var previous))
            {
                previous.Cts.Cancel();
                try { previous.Socket.Abort(); } catch { }
            }
            _devices[serial] = connection;

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, connection.Cts.Token);
            var writer = WriteLoopAsync(connection, linked.Token);
            var startId = first.TryGetProperty("id", out var startIdElement) && startIdElement.TryGetInt32(out var parsedStartId)
                ? parsedStartId : 0;
            await QueuePriorityAsync(connection, new { id = startId, target = serial, type = "ack", data = "OK" }, linked.Token);
            await MarkOnlineAsync(connection, linked.Token);

            while (socket.State == WebSocketState.Open && !linked.IsCancellationRequested)
            {
                var text = await ReadTextAsync(socket, linked.Token);
                if (text == null) break;
                await HandleIncomingAsync(connection, text, linked.Token);
            }

            connection.Cts.Cancel();
            connection.Priority.Writer.TryComplete();
            connection.Normal.Writer.TryComplete();
            try { await writer; } catch { }
        }
        catch (OperationCanceledException) { }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "JSON inválido recebido em /telemetria.");
            if (socket.State == WebSocketState.Open) await ClosePolicyViolationAsync(socket, "JSON inválido.");
        }
        catch (WebSocketException ex)
        {
            _logger.LogDebug(ex, "Conexão ESP encerrada.");
        }
        finally
        {
            if (connection != null && _devices.TryGetValue(connection.Serial, out var current) && ReferenceEquals(current, connection))
            {
                _devices.TryRemove(connection.Serial, out _);
                await MarkOfflineAsync(connection);
            }
            try { socket.Abort(); } catch { }
        }
    }

    public async Task<DeviceCommandResult> SendCommandAsync(string serial, object data, CancellationToken cancellationToken = default)
    {
        if (!_devices.TryGetValue(NormalizeSerial(serial), out var connection) || connection.Socket.State != WebSocketState.Open)
            return new DeviceCommandResult(false, 0, 0, "machine_offline");

        await connection.CommandGate.WaitAsync(cancellationToken);
        var commandId = 0;
        try
        {
            var id = Interlocked.Increment(ref connection.LastServerMessageId);
            commandId = id;
            var json = JsonSerializer.Serialize(new { id, target = connection.Serial, type = "msg", data }, JsonOptions);
            var acknowledgement = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.PendingAcks[id] = acknowledgement;

            for (var attempt = 1; attempt <= 3; attempt++)
            {
                await connection.Normal.Writer.WriteAsync(json, cancellationToken);
                try
                {
                    await acknowledgement.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                    return new DeviceCommandResult(true, id, attempt);
                }
                catch (TimeoutException) when (attempt < 3) { }
            }

            return new DeviceCommandResult(false, id, 3, "ack_timeout");
        }
        finally
        {
            if (commandId != 0) connection.PendingAcks.TryRemove(commandId, out _);
            connection.CommandGate.Release();
        }
    }

    public async Task<DeviceStatusResult> SendStatusRequestAsync(
        string serial,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (!_devices.TryGetValue(NormalizeSerial(serial), out var connection) ||
            connection.Socket.State != WebSocketState.Open)
        {
            return new DeviceStatusResult(false, 0, null, null, null, "machine_offline");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connection.Cts.Token);
        await connection.CommandGate.WaitAsync(linked.Token);
        PendingStatusRequest? pending = null;
        try
        {
            var id = Interlocked.Increment(ref connection.LastServerMessageId);
            var json = JsonSerializer.Serialize(
                new { id, target = connection.Serial, type = "msg", data = "MDB_STATUS" },
                JsonOptions);
            pending = new PendingStatusRequest(
                id,
                new TaskCompletionSource<InboundStatusResponse>(TaskCreationOptions.RunContinuationsAsynchronously));
            Volatile.Write(ref connection.PendingStatusRequest, pending);

            await connection.Normal.Writer.WriteAsync(json, linked.Token);

            InboundStatusResponse response;
            try
            {
                response = await pending.Completion.Task.WaitAsync(timeout, linked.Token);
            }
            catch (TimeoutException)
            {
                return new DeviceStatusResult(false, id, null, null, null, "response_timeout");
            }

            var normalized = MdbStatuses.Normalize(response.Status);
            return normalized == null
                ? new DeviceStatusResult(false, id, null, response.DataJson, response.ReceivedAt, "invalid_mdb_status")
                : new DeviceStatusResult(true, id, normalized, response.DataJson, response.ReceivedAt);
        }
        catch (OperationCanceledException) when (connection.Cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return new DeviceStatusResult(false, pending?.MessageId ?? 0, null, null, null, "machine_offline");
        }
        finally
        {
            if (pending != null)
            {
                Interlocked.CompareExchange(ref connection.PendingStatusRequest, null, pending);
            }
            connection.CommandGate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = Enumerable.Range(0, 4).Select(_ => ProcessInboundAsync(stoppingToken)).ToArray();
        var heartbeat = MonitorHeartbeatAsync(stoppingToken);
        await Task.WhenAll(workers.Append(heartbeat));
    }

    private async Task ProcessInboundAsync(CancellationToken cancellationToken)
    {
        await foreach (var message in _inbound.Reader.ReadAllAsync(cancellationToken))
        {
            var gate = _machineMessageGates.GetOrAdd(message.MachineId, static _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken);
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var handler = scope.ServiceProvider.GetRequiredService<SessionOrchestrator>();
                await handler.HandleDeviceMessageAsync(message, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao processar mensagem da máquina {MachineId}.", message.MachineId);
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private async Task MonitorHeartbeatAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var limit = DateTime.UtcNow.AddSeconds(-90);
            foreach (var connection in _devices.Values)
            {
                if (connection.LastSeenAt < limit)
                {
                    _logger.LogWarning("Heartbeat expirado para {Serial}.", connection.Serial);
                    connection.Cts.Cancel();
                    try { connection.Socket.Abort(); } catch { }
                }
            }
        }
    }

    private async Task HandleIncomingAsync(DeviceConnection connection, string text, CancellationToken cancellationToken)
    {
        connection.LastSeenAt = DateTime.UtcNow;
        if (connection.LastSeenAt - connection.LastPersistedSeenAt >= TimeSpan.FromMinutes(1))
        {
            connection.LastPersistedSeenAt = connection.LastSeenAt;
            _ = PersistLastSeenAsync(connection);
        }

        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        if (!root.TryGetProperty("type", out var typeElement)) return;
        var type = typeElement.GetString();
        var id = root.TryGetProperty("id", out var idElement) && idElement.TryGetInt32(out var parsedId) ? parsedId : 0;

        if (root.TryGetProperty("target", out var targetElement) &&
            !string.IsNullOrWhiteSpace(targetElement.GetString()) &&
            NormalizeSerial(targetElement.GetString()!) != connection.Serial)
        {
            throw new WebSocketException("Target divergente do serial autenticado.");
        }

        if (type == "ack")
        {
            if (connection.PendingAcks.TryRemove(id, out var pending)) pending.TrySetResult(true);
            return;
        }

        if (type == "ping")
        {
            connection.Signal = root.TryGetProperty("signal", out var signal) && signal.TryGetInt32(out var parsedSignal)
                ? parsedSignal : connection.Signal;
            await QueuePriorityAsync(connection, new
            {
                id,
                type = "pong",
                serverTime = DateTime.UtcNow.ToString("O"),
                status = "ok"
            }, cancellationToken);
            return;
        }

        if (type != "msg" || !root.TryGetProperty("data", out var data)) return;

        var isStatusResponse = data.ValueKind == JsonValueKind.Object &&
                               data.TryGetProperty("command", out var commandElement) &&
                               string.Equals(commandElement.GetString(), "status", StringComparison.OrdinalIgnoreCase);
        if (isStatusResponse)
        {
            var status = data.TryGetProperty("status", out var statusElement)
                ? statusElement.GetString()
                : null;
            var pendingStatus = Volatile.Read(ref connection.PendingStatusRequest);
            if (pendingStatus?.MessageId == id)
            {
                pendingStatus.Completion.TrySetResult(new InboundStatusResponse(
                    status,
                    data.GetRawText(),
                    DateTime.UtcNow));
            }
        }

        await QueuePriorityAsync(connection, new { id, target = connection.Serial, type = "ack", data = "OK" }, cancellationToken);

        // A resposta de MDB_STATUS pode reutilizar o id gerado pelo servidor. Ela não
        // participa da deduplicação dos eventos iniciados pelo dispositivo, pois os dois
        // lados podem possuir sequências de ids independentes.
        if (isStatusResponse && !connection.ReceivedStatusIds.TryAdd(id, 0)) return;
        if (isStatusResponse && connection.ReceivedStatusIds.Count > 512)
        {
            foreach (var oldId in connection.ReceivedStatusIds.Keys.OrderBy(x => x).Take(128))
                connection.ReceivedStatusIds.TryRemove(oldId, out _);
        }
        if (!isStatusResponse && !connection.ReceivedIds.TryAdd(id, 0)) return;
        if (!isStatusResponse && connection.ReceivedIds.Count > 512)
        {
            foreach (var oldId in connection.ReceivedIds.Keys.OrderBy(x => x).Take(128))
                connection.ReceivedIds.TryRemove(oldId, out _);
        }

        var inbound = new InboundDeviceMessage(
            connection.MachineId,
            connection.CompanyId,
            connection.Serial,
            connection.ConnectionId,
            id,
            data.GetRawText(),
            DateTime.UtcNow);
        await _inbound.Writer.WriteAsync(inbound, cancellationToken);
        await _panelHub.BroadcastMachineAsync(connection.CompanyId, connection.MachineId, new
        {
            type = "telemetry.received",
            machineId = connection.MachineId,
            serial = connection.Serial,
            messageId = id,
            data = JsonSerializer.Deserialize<JsonElement>(inbound.DataJson),
            receivedAt = inbound.ReceivedAt
        });
    }

    private static async Task QueuePriorityAsync(DeviceConnection connection, object payload, CancellationToken cancellationToken) =>
        await connection.Priority.Writer.WriteAsync(JsonSerializer.Serialize(payload, JsonOptions), cancellationToken);

    private static async Task WriteLoopAsync(DeviceConnection connection, CancellationToken cancellationToken)
    {
        Task<bool>? highReady = null;
        Task<bool>? normalReady = null;

        while (!cancellationToken.IsCancellationRequested && connection.Socket.State == WebSocketState.Open)
        {
            if (connection.Priority.Reader.TryRead(out var json) || connection.Normal.Reader.TryRead(out json))
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                await connection.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
                continue;
            }

            highReady ??= connection.Priority.Reader.WaitToReadAsync(cancellationToken).AsTask();
            normalReady ??= connection.Normal.Reader.WaitToReadAsync(cancellationToken).AsTask();

            await Task.WhenAny(highReady, normalReady);

            if (highReady.IsCompleted) highReady = null;
            if (normalReady.IsCompleted) normalReady = null;
        }
    }

    private async Task<Machine?> FindMachineAsync(string normalizedSerial, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Machines.AsNoTracking().FirstOrDefaultAsync(m => m.NormalizedSerialNumber == normalizedSerial, cancellationToken);
    }

    private async Task MarkOnlineAsync(DeviceConnection connection, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var state = await db.MachineConnectionStates.FirstOrDefaultAsync(x => x.MachineId == connection.MachineId, cancellationToken);
        if (state == null)
        {
            state = new MachineConnectionState { MachineId = connection.MachineId, CompanyId = connection.CompanyId };
            db.MachineConnectionStates.Add(state);
        }
        state.IsOnline = true;
        state.ConnectionId = connection.ConnectionId;
        state.ConnectedAt = DateTime.UtcNow;
        state.LastSeenAt = DateTime.UtcNow;
        state.MdbStatusRequestAt = null;
        state.MdbStatusRequestMessageId = null;
        await db.SaveChangesAsync(cancellationToken);
        await _panelHub.BroadcastCompanyAsync(connection.CompanyId, new
        {
            type = "connection.updated", machineId = connection.MachineId, online = true,
            connectedAt = state.ConnectedAt, lastSeenAt = state.LastSeenAt
        });
    }

    private async Task MarkOfflineAsync(DeviceConnection connection)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var state = await db.MachineConnectionStates.FirstOrDefaultAsync(x => x.MachineId == connection.MachineId);
            if (state != null && state.ConnectionId == connection.ConnectionId)
            {
                state.IsOnline = false;
                state.ConnectionId = null;
                state.DisconnectedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }
            await _panelHub.BroadcastCompanyAsync(connection.CompanyId, new
            {
                type = "connection.updated", machineId = connection.MachineId, online = false,
                disconnectedAt = DateTime.UtcNow
            });
        }
        catch (Exception ex) { _logger.LogError(ex, "Falha ao persistir desconexão de {Serial}.", connection.Serial); }
    }

    private async Task PersistLastSeenAsync(DeviceConnection connection)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var state = await db.MachineConnectionStates.FirstOrDefaultAsync(x => x.MachineId == connection.MachineId);
            if (state != null && state.ConnectionId == connection.ConnectionId)
            {
                state.LastSeenAt = connection.LastSeenAt;
                await db.SaveChangesAsync();
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Falha ao atualizar lastSeen de {Serial}.", connection.Serial); }
    }

    private static async Task<string?> ReadTextAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[8192];
        do
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text) continue;
            await stream.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken);
            if (stream.Length > 64 * 1024) throw new WebSocketException("Payload excedeu 64 KB.");
            if (result.EndOfMessage) break;
        } while (true);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static async Task ClosePolicyViolationAsync(WebSocket socket, string reason)
    {
        if (socket.State == WebSocketState.Open)
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, reason, CancellationToken.None);
    }

    public static string NormalizeSerial(string serial) => serial.Trim().ToUpperInvariant();

    private sealed class DeviceConnection
    {
        public DeviceConnection(string machineId, Guid companyId, string serial, WebSocket socket)
        {
            MachineId = machineId;
            CompanyId = companyId;
            Serial = serial;
            Socket = socket;
        }

        public string MachineId { get; }
        public Guid CompanyId { get; }
        public string Serial { get; }
        public string ConnectionId { get; } = Guid.NewGuid().ToString("N");
        public WebSocket Socket { get; }
        public CancellationTokenSource Cts { get; } = new();
        public SemaphoreSlim CommandGate { get; } = new(1, 1);
        public ConcurrentDictionary<int, TaskCompletionSource<bool>> PendingAcks { get; } = new();
        public PendingStatusRequest? PendingStatusRequest;
        public ConcurrentDictionary<int, byte> ReceivedIds { get; } = new();
        public ConcurrentDictionary<int, byte> ReceivedStatusIds { get; } = new();
        public Channel<string> Priority { get; } = Channel.CreateBounded<string>(1000);
        public Channel<string> Normal { get; } = Channel.CreateBounded<string>(1000);
        public int LastServerMessageId;
        public int Signal;
        public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
        public DateTime LastPersistedSeenAt { get; set; } = DateTime.MinValue;
    }

    private sealed record PendingStatusRequest(
        int MessageId,
        TaskCompletionSource<InboundStatusResponse> Completion);

    private sealed record InboundStatusResponse(
        string? Status,
        string DataJson,
        DateTime ReceivedAt);
}
