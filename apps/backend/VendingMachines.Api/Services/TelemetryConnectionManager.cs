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
    private static long _lastCommandDispatchCleanupUtcTicks;
    private readonly ConcurrentDictionary<string, DeviceConnection> _devices = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TelemetryPanelHub _panelHub;
    private readonly ILogger<TelemetryConnectionManager> _logger;
    private readonly BackgroundWorkerHealthRegistry? _health;
    public string NodeId { get; } = Guid.NewGuid().ToString("N");
    private readonly bool _requireDeviceCredentials;
    private readonly object _shutdownLock = new();
    private readonly CancellationTokenSource _inboundWorkerStop = new();
    private Task[] _inboundWorkers = Array.Empty<Task>();
    private Task? _drainTask;
    private volatile bool _isStopping;

    public TelemetryConnectionManager(
        IServiceScopeFactory scopeFactory,
        TelemetryPanelHub panelHub,
        ILogger<TelemetryConnectionManager> logger,
        IConfiguration? configuration = null,
        IHostApplicationLifetime? lifetime = null,
        BackgroundWorkerHealthRegistry? health = null)
    {
        _scopeFactory = scopeFactory;
        _panelHub = panelHub;
        _logger = logger;
        _health = health;
        _requireDeviceCredentials = configuration?.GetValue<bool>("Telemetry:RequireDeviceCredentials") == true;
        lifetime?.ApplicationStopping.Register(() =>
        {
            try
            {
                _logger.LogInformation("Iniciando drenagem de telemetria antes do encerramento do servidor.");
                // ApplicationStopping callbacks run before Kestrel stops active requests.
                // Wait here so long-lived WebSocket requests remain alive during the drain.
                StartDrain(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha na drenagem de telemetria durante o encerramento.");
            }
        });
    }

    public bool IsOnline(string serial) =>
        _devices.TryGetValue(NormalizeSerial(serial), out var connection) && connection.Socket.State == WebSocketState.Open;

    public async Task DisconnectAsync(string serial, string reason)
    {
        if (!_devices.TryRemove(NormalizeSerial(serial), out var connection)) return;

        connection.Cts.Cancel();
        connection.Priority.Writer.TryComplete();
        connection.Normal.Writer.TryComplete();
        try
        {
            if (connection.Socket.State == WebSocketState.Open)
            {
                await connection.Socket.CloseAsync(
                    WebSocketCloseStatus.PolicyViolation,
                    reason,
                    CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Não foi possível fechar a conexão revogada da máquina {Serial}.", serial);
        }
        finally
        {
            try { connection.Socket.Abort(); } catch { }
            await MarkOfflineAsync(connection);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await StartDrain(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "A drenagem de telemetria falhou; as mensagens aceitas continuam registradas na inbox durável.");
        }
        finally
        {
            await base.StopAsync(cancellationToken);
        }
    }

    private Task StartDrain(CancellationToken cancellationToken)
    {
        lock (_shutdownLock)
        {
            _isStopping = true;
            if (_drainTask != null) return _drainTask;
            _drainTask = DrainConnectionsAsync(cancellationToken);
            return _drainTask;
        }
    }

    private async Task DrainConnectionsAsync(CancellationToken cancellationToken)
    {
        using var drainTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // A server command can wait for up to three five-second ACK attempts. Leave
        // enough time for that operation to finish before interrupting its writer.
        drainTimeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var activeConnections = _devices.Values.ToArray();
            await Task.WhenAll(activeConnections.Select(connection => DrainDeviceAsync(connection, drainTimeout.Token)));

            while (await CountUnfinishedInboxMessagesAsync(drainTimeout.Token) > 0)
                await Task.Delay(TimeSpan.FromMilliseconds(250), drainTimeout.Token);
        }
        catch (OperationCanceledException) when (drainTimeout.IsCancellationRequested)
        {
            try
            {
                using var countTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var pending = await CountUnfinishedInboxMessagesAsync(countTimeout.Token);
                _logger.LogCritical(
                    "O prazo de drenagem terminou com mensagens MDB ainda pendentes. Itens persistidos: {PendingCount}; serão retomados na próxima inicialização.",
                    pending);
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Não foi possível contar mensagens MDB pendentes no encerramento; o estado durável será retomado na próxima inicialização.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "A drenagem de telemetria falhou; eventos confirmados permanecem na inbox durável para retomada.");
        }
        finally
        {
            _inboundWorkerStop.Cancel();
            try { await Task.WhenAll(Volatile.Read(ref _inboundWorkers)); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logger.LogError(ex, "Worker de inbox encerrou com erro durante a drenagem."); }
        }
    }

    private async Task DrainDeviceAsync(DeviceConnection connection, CancellationToken cancellationToken)
    {
        var commandGateAcquired = false;
        try
        {
            commandGateAcquired = await connection.CommandGate.WaitAsync(TimeSpan.FromSeconds(16), cancellationToken);
        }
        catch (OperationCanceledException) { }

        if (!commandGateAcquired)
        {
            _logger.LogWarning(
                "A máquina {Serial} não concluiu o comando em andamento dentro do prazo de drenagem.",
                connection.Serial);
        }
        else
        {
            // _isStopping prevents another command from starting after this point.
            connection.CommandGate.Release();
            _logger.LogInformation("Comandos em andamento concluídos para {Serial}; iniciando fechamento WebSocket.", connection.Serial);
        }

        var writerStopped = connection.WriterTask == null;
        var inboundGateAcquired = false;
        try
        {
            await connection.InboundGate.WaitAsync(cancellationToken);
            inboundGateAcquired = true;
            await connection.OutboundGate.WaitAsync(cancellationToken);
            try
            {
                connection.DrainRequested = true;
                connection.DrainSignal.TrySetResult();
            }
            finally
            {
                connection.OutboundGate.Release();
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (inboundGateAcquired) connection.InboundGate.Release();
        }

        if (connection.WriterTask != null)
        {
            try
            {
                await connection.CloseSent.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
                writerStopped = true;
            }
            catch (TimeoutException) { }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Falha ao concluir o fechamento WebSocket de {Serial}.", connection.Serial);
            }
        }

        if (!writerStopped)
        {
            _logger.LogWarning("Writer de {Serial} não concluiu a drenagem; abortando a conexão para permitir reconexão.", connection.Serial);
            connection.WriterCts.Cancel();
            try { connection.Socket.Abort(); } catch { }
        }

        try
        {
            await connection.Disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            return;
        }
        catch (TimeoutException) { }
        catch (OperationCanceledException) { }

        connection.Cts.Cancel();
        try { connection.Socket.Abort(); } catch { }
        await MarkOfflineAsync(connection);
    }

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
                _logger.LogWarning("Handshake MDB rejeitado: código {RejectionCode}.", "invalid_start_frame");
                await ClosePolicyViolationAsync(socket, "A primeira mensagem deve ser start com target.");
                return;
            }

            var serial = NormalizeSerial(target.GetString()!);
            var machine = await FindMachineAsync(serial, requestAborted);
            if (machine == null || !machine.CompanyId.HasValue)
            {
                _logger.LogWarning("Handshake MDB rejeitado: código {RejectionCode}.", "unknown_machine");
                await ClosePolicyViolationAsync(socket, "Máquina não cadastrada.");
                return;
            }

            var credential = first.TryGetProperty("credential", out var credentialElement) &&
                credentialElement.ValueKind == JsonValueKind.String
                    ? credentialElement.GetString()
                    : null;
            if (_requireDeviceCredentials || credential is not null)
            {
                if (!DeviceCredentialService.Verify(
                        credential ?? string.Empty,
                        machine.TelemetryCredentialHash,
                        machine.TelemetryCredentialRevokedAt))
                {
                    _logger.LogWarning("Credencial de telemetria rejeitada para a máquina {MachineId}.", machine.Id);
                    await ClosePolicyViolationAsync(socket, "Credencial de telemetria inválida.");
                    return;
                }
            }

            var newConnection = new DeviceConnection(machine.Id, machine.CompanyId.Value, serial, socket);
            if (!TryRegisterDevice(newConnection))
            {
                await socket.CloseAsync(
                    WebSocketCloseStatus.EndpointUnavailable,
                    "Máquina já conectada ou servidor em atualização. Reconecte em alguns instantes.",
                    requestAborted);
                return;
            }
            connection = newConnection;

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, connection.Cts.Token);
            using var writerLinked = CancellationTokenSource.CreateLinkedTokenSource(linked.Token, connection.WriterCts.Token);
            var writer = WriteLoopAsync(connection, writerLinked.Token);
            connection.WriterTask = writer;
            var startId = first.TryGetProperty("id", out var startIdElement) && startIdElement.TryGetInt32(out var parsedStartId)
                ? parsedStartId : 0;
            await QueuePriorityAsync(connection, new { id = startId, target = serial, type = "ack", data = "OK" }, linked.Token);
            await MarkOnlineAsync(connection, linked.Token);

            while (socket.State == WebSocketState.Open && !linked.IsCancellationRequested)
            {
                var text = await ReadTextAsync(socket, linked.Token);
                if (text == null) break;
                await connection.InboundGate.WaitAsync(linked.Token);
                try
                {
                    await HandleIncomingAsync(connection, text, linked.Token);
                }
                finally
                {
                    connection.InboundGate.Release();
                }
            }

            connection.Cts.Cancel();
            connection.Priority.Writer.TryComplete();
            connection.Normal.Writer.TryComplete();
            try { await writer; } catch { }
        }
        catch (OperationCanceledException) { }
        catch (DeviceMessageInboxFullException ex)
        {
            _logger.LogCritical(ex, "Fila durável de telemetria cheia; encerrando {Serial} sem confirmar a mensagem para que o dispositivo tente novamente.", connection?.Serial ?? "desconhecida");
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await socket.CloseAsync(
                        WebSocketCloseStatus.EndpointUnavailable,
                        "Fila de telemetria cheia. Reconecte em instantes.",
                        closeTimeout.Token);
                }
            }
            catch (Exception closeException) when (closeException is WebSocketException or InvalidOperationException or OperationCanceledException) { }
        }
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
            connection?.Disconnected.TrySetResult();
        }
    }

    public async Task<DeviceCommandResult> SendCommandAsync(string serial, object data, CancellationToken cancellationToken = default)
    {
        if (_isStopping)
            return new DeviceCommandResult(false, 0, 0, "server_restarting");

        var normalizedSerial = NormalizeSerial(serial);
        if (_devices.TryGetValue(normalizedSerial, out var connection) && connection.Socket.State == WebSocketState.Open)
            return await SendCommandLocallyAsync(connection, data, cancellationToken);

        try
        {
            return await EnqueueRemoteCommandAsync(normalizedSerial, data, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Não foi possível consultar o roteamento do comando MDB para {Serial}.", normalizedSerial);
            return new DeviceCommandResult(false, 0, 0, "command_routing_unavailable");
        }
    }

    private async Task<DeviceCommandResult> SendCommandLocallyAsync(
        DeviceConnection connection,
        object data,
        CancellationToken cancellationToken)
    {
        if (_isStopping)
            return new DeviceCommandResult(false, 0, 0, "server_restarting");
        if (connection.Socket.State != WebSocketState.Open)
            return new DeviceCommandResult(false, 0, 0, "machine_offline");

        await connection.CommandGate.WaitAsync(cancellationToken);
        var commandId = 0;
        try
        {
            if (_isStopping)
                return new DeviceCommandResult(false, 0, 0, "server_restarting");

            var id = Interlocked.Increment(ref connection.LastServerMessageId);
            commandId = id;
            var json = JsonSerializer.Serialize(new { id, target = connection.Serial, type = "msg", data }, JsonOptions);
            var acknowledgement = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.PendingAcks[id] = acknowledgement;

            for (var attempt = 1; attempt <= 3; attempt++)
            {
                await QueueOutboundAsync(connection, connection.Normal, json, cancellationToken);
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

    private async Task<DeviceCommandResult> EnqueueRemoteCommandAsync(
        string normalizedSerial,
        object data,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var machine = await db.Machines.AsNoTracking()
            .Where(x => x.NormalizedSerialNumber == normalizedSerial && x.CompanyId != null)
            .Select(x => new { x.Id, CompanyId = x.CompanyId!.Value })
            .FirstOrDefaultAsync(cancellationToken);
        if (machine == null)
            return new DeviceCommandResult(false, 0, 0, "machine_offline");

        var route = await db.MachineConnectionStates.AsNoTracking()
            .Where(x => x.MachineId == machine.Id && x.CompanyId == machine.CompanyId)
            .Select(x => new { x.IsOnline, x.LastSeenAt, x.OwnerNodeId })
            .FirstOrDefaultAsync(cancellationToken);
        if (route?.IsOnline != true || !route.LastSeenAt.HasValue || route.LastSeenAt < DateTime.UtcNow.AddSeconds(-90))
            return new DeviceCommandResult(false, 0, 0, "machine_offline");
        if (string.IsNullOrWhiteSpace(route.OwnerNodeId))
            return new DeviceCommandResult(false, 0, 0, "machine_owner_unknown");
        if (route.OwnerNodeId == NodeId)
            return new DeviceCommandResult(false, 0, 0, "machine_offline");

        var dispatch = new TelemetryCommandDispatch
        {
            CompanyId = machine.CompanyId,
            MachineId = machine.Id,
            OwnerNodeId = route.OwnerNodeId,
            PayloadJson = JsonSerializer.Serialize(data, JsonOptions),
            Status = "Pending",
            CreatedAt = DateTime.UtcNow
        };
        db.TelemetryCommandDispatches.Add(dispatch);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The database may have committed even when the client lost the commit
            // acknowledgement. Never report this as a safe-to-retry offline command.
            _logger.LogError(ex, "Resultado do registro do comando remoto para {Serial} é incerto.", normalizedSerial);
            return new DeviceCommandResult(false, 0, 0, "command_outcome_unknown");
        }

        var deadline = DateTime.UtcNow.AddSeconds(20);
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = await db.TelemetryCommandDispatches.AsNoTracking()
                    .Where(x => x.Id == dispatch.Id)
                    .Select(x => new { x.Status, x.MessageId, x.Attempts, x.Success, x.Error })
                    .FirstOrDefaultAsync(cancellationToken);
                if (current == null)
                    return new DeviceCommandResult(false, 0, 0, "command_outcome_unknown");
                if (current.Status is "Completed" or "Failed" or "Indeterminate")
                    return new DeviceCommandResult(current.Success == true, current.MessageId ?? 0, current.Attempts ?? 0, current.Error);

                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
            }

            // A request that is still Pending is safe to cancel. Once Processing, it may
            // already have reached the machine and must never be replayed automatically.
            var cancelled = await db.TelemetryCommandDispatches
                .Where(x => x.Id == dispatch.Id && x.Status == "Pending")
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, "Cancelled")
                    .SetProperty(x => x.CompletedAt, DateTime.UtcNow)
                    .SetProperty(x => x.Error, "dispatch_timeout_before_send"), cancellationToken);
            if (cancelled > 0)
                return new DeviceCommandResult(false, 0, 0, "dispatch_timeout_before_send");

            return new DeviceCommandResult(false, 0, 0, "command_outcome_unknown");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Acompanhamento do comando remoto {DispatchId} falhou após registro; resultado incerto.", dispatch.Id);
            return new DeviceCommandResult(false, 0, 0, "command_outcome_unknown");
        }
    }

    private async Task<DeviceStatusResult> EnqueueRemoteStatusRequestAsync(
        string normalizedSerial,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var machine = await db.Machines.AsNoTracking()
            .Where(x => x.NormalizedSerialNumber == normalizedSerial && x.CompanyId != null)
            .Select(x => new { x.Id, CompanyId = x.CompanyId!.Value })
            .FirstOrDefaultAsync(cancellationToken);
        if (machine == null)
            return new DeviceStatusResult(false, 0, null, null, null, "machine_offline");

        var route = await db.MachineConnectionStates.AsNoTracking()
            .Where(x => x.MachineId == machine.Id && x.CompanyId == machine.CompanyId)
            .Select(x => new { x.IsOnline, x.LastSeenAt, x.OwnerNodeId })
            .FirstOrDefaultAsync(cancellationToken);
        if (route?.IsOnline != true || !route.LastSeenAt.HasValue || route.LastSeenAt < DateTime.UtcNow.AddSeconds(-90))
            return new DeviceStatusResult(false, 0, null, null, null, "machine_offline");
        if (string.IsNullOrWhiteSpace(route.OwnerNodeId))
            return new DeviceStatusResult(false, 0, null, null, null, "machine_owner_unknown");
        if (route.OwnerNodeId == NodeId)
            return new DeviceStatusResult(false, 0, null, null, null, "machine_offline");

        var timeoutMilliseconds = Math.Clamp((int)timeout.TotalMilliseconds, 1000, 30000);
        var dispatch = new TelemetryCommandDispatch
        {
            CompanyId = machine.CompanyId,
            MachineId = machine.Id,
            OwnerNodeId = route.OwnerNodeId,
            Operation = "MdbStatus",
            TimeoutMilliseconds = timeoutMilliseconds,
            PayloadJson = "\"MDB_STATUS\"",
            Status = "Pending",
            CreatedAt = DateTime.UtcNow
        };
        db.TelemetryCommandDispatches.Add(dispatch);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Resultado do registro da consulta MDB remota para {Serial} é incerto.", normalizedSerial);
            return new DeviceStatusResult(false, 0, null, null, null, "response_timeout");
        }

        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds + 10000);
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                var current = await db.TelemetryCommandDispatches.AsNoTracking()
                    .Where(x => x.Id == dispatch.Id)
                    .Select(x => new
                    {
                        x.Status, x.MessageId, x.Success, x.Error,
                        x.ResponseStatus, x.ResponseDataJson, x.ResponseAt
                    })
                    .FirstOrDefaultAsync(cancellationToken);
                if (current == null)
                    return new DeviceStatusResult(false, 0, null, null, null, "response_timeout");
                if (current.Status is "Completed" or "Failed" or "Indeterminate")
                {
                    return new DeviceStatusResult(
                        current.Success == true,
                        current.MessageId ?? 0,
                        current.ResponseStatus,
                        current.ResponseDataJson,
                        current.ResponseAt,
                        current.Error);
                }
                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
            }

            var cancelled = await db.TelemetryCommandDispatches
                .Where(x => x.Id == dispatch.Id && x.Status == "Pending")
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, "Cancelled")
                    .SetProperty(x => x.CompletedAt, DateTime.UtcNow)
                    .SetProperty(x => x.Error, "response_timeout"), cancellationToken);
            _ = cancelled;
            return new DeviceStatusResult(false, 0, null, null, null, "response_timeout");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Acompanhamento da consulta MDB remota {DispatchId} falhou.", dispatch.Id);
            return new DeviceStatusResult(false, 0, null, null, null, "response_timeout");
        }
    }

    public async Task<bool> IsOnlineAsync(string serial, CancellationToken cancellationToken = default)
    {
        var normalizedSerial = NormalizeSerial(serial);
        if (_devices.TryGetValue(normalizedSerial, out var local) && local.Socket.State == WebSocketState.Open)
            return true;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var freshnessCutoff = DateTime.UtcNow.AddSeconds(-90);
            return await db.Machines.AsNoTracking()
                .Where(machine => machine.NormalizedSerialNumber == normalizedSerial)
                .Join(db.MachineConnectionStates.AsNoTracking(), machine => machine.Id, state => state.MachineId,
                    (machine, state) => new
                    {
                        MachineCompanyId = machine.CompanyId,
                        StateCompanyId = state.CompanyId,
                        state.IsOnline,
                        state.LastSeenAt
                    })
                .AnyAsync(state => state.MachineCompanyId == state.StateCompanyId &&
                                   state.IsOnline && state.LastSeenAt >= freshnessCutoff,
                    cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Não foi possível consultar a presença compartilhada da máquina {Serial}.", normalizedSerial);
            return false;
        }
    }

    public async Task<HashSet<string>> GetOnlineSerialsAsync(
        IEnumerable<string> serials,
        CancellationToken cancellationToken = default)
    {
        var requested = serials.Select(NormalizeSerial).Where(x => x.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var online = _devices.Values
            .Where(connection => connection.Socket.State == WebSocketState.Open && requested.Contains(connection.Serial))
            .Select(connection => connection.Serial)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remaining = requested.Where(serial => !online.Contains(serial)).ToArray();
        if (remaining.Length == 0) return online;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var freshnessCutoff = DateTime.UtcNow.AddSeconds(-90);
            var remote = await db.Machines.AsNoTracking()
                .Where(machine => machine.NormalizedSerialNumber != null && remaining.Contains(machine.NormalizedSerialNumber))
                .Join(db.MachineConnectionStates.AsNoTracking(), machine => machine.Id, state => state.MachineId,
                    (machine, state) => new
                    {
                        MachineCompanyId = machine.CompanyId,
                        machine.NormalizedSerialNumber,
                        StateCompanyId = state.CompanyId,
                        state.IsOnline,
                        state.OwnerNodeId,
                        state.LastSeenAt
                    })
                .Where(state => state.MachineCompanyId == state.StateCompanyId &&
                                state.IsOnline && state.LastSeenAt >= freshnessCutoff)
                .Select(state => state.NormalizedSerialNumber!)
                .ToListAsync(cancellationToken);
            online.UnionWith(remote);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Não foi possível consultar a presença compartilhada das máquinas da página.");
        }
        return online;
    }

    public async Task<DeviceStatusResult> SendStatusRequestAsync(
        string serial,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (_isStopping)
            return new DeviceStatusResult(false, 0, null, null, null, "server_restarting");
        var normalizedSerial = NormalizeSerial(serial);
        if (_devices.TryGetValue(normalizedSerial, out var connection) && connection.Socket.State == WebSocketState.Open)
            return await SendStatusRequestLocallyAsync(connection, timeout, cancellationToken);

        try
        {
            return await EnqueueRemoteStatusRequestAsync(normalizedSerial, timeout, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Não foi possível consultar o roteamento do status MDB para {Serial}.", normalizedSerial);
            return new DeviceStatusResult(false, 0, null, null, null, "status_routing_unavailable");
        }
    }

    private async Task<DeviceStatusResult> SendStatusRequestLocallyAsync(
        DeviceConnection connection,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (_isStopping)
            return new DeviceStatusResult(false, 0, null, null, null, "server_restarting");
        if (connection.Socket.State != WebSocketState.Open)
            return new DeviceStatusResult(false, 0, null, null, null, "machine_offline");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connection.Cts.Token);
        await connection.CommandGate.WaitAsync(linked.Token);
        PendingStatusRequest? pending = null;
        try
        {
            if (_isStopping)
                return new DeviceStatusResult(false, 0, null, null, null, "server_restarting");

            var id = Interlocked.Increment(ref connection.LastServerMessageId);
            var json = JsonSerializer.Serialize(
                new { id, target = connection.Serial, type = "msg", data = "MDB_STATUS" },
                JsonOptions);
            pending = new PendingStatusRequest(
                id,
                new TaskCompletionSource<InboundStatusResponse>(TaskCreationOptions.RunContinuationsAsynchronously));
            Volatile.Write(ref connection.PendingStatusRequest, pending);

            await QueueOutboundAsync(connection, connection.Normal, json, linked.Token);

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
        _health?.ReportStarted(BackgroundWorkerNames.Telemetry);
        using var linkedWorkers = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _inboundWorkerStop.Token);
        var workers = Enumerable.Range(1, 4).Select(number => ProcessInboundAsync(number, linkedWorkers.Token)).ToArray();
        Volatile.Write(ref _inboundWorkers, workers);
        var heartbeat = MonitorHeartbeatAsync(stoppingToken);
        var commandDispatcher = ProcessCommandDispatchesAsync(stoppingToken);
        try { await Task.WhenAll(workers.Append(heartbeat).Append(commandDispatcher)); }
        finally { _health?.ReportStopped(BackgroundWorkerNames.Telemetry); }
    }

    private async Task ProcessCommandDispatchesAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await CleanupCommandDispatchesIfDueAsync(db, cancellationToken);
                var candidate = await db.TelemetryCommandDispatches.AsNoTracking()
                    .Where(x => x.OwnerNodeId == NodeId && x.Status == "Pending")
                    .OrderBy(x => x.CreatedAt)
                    .Select(x => x.Id)
                    .FirstOrDefaultAsync(cancellationToken);
                if (candidate == Guid.Empty)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
                    continue;
                }

                var processingAt = DateTime.UtcNow;
                var claimed = await db.TelemetryCommandDispatches
                    .Where(x => x.Id == candidate && x.OwnerNodeId == NodeId && x.Status == "Pending")
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.Status, "Processing")
                        .SetProperty(x => x.ProcessingStartedAt, processingAt), cancellationToken);
                if (claimed == 0) continue;

                await ExecuteCommandDispatchAsync(candidate, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha no roteador durável de comandos MDB do nó {NodeId}.", NodeId);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
    }

    private static async Task CleanupCommandDispatchesIfDueAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var nowTicks = now.Ticks;
        var lastCleanup = Volatile.Read(ref _lastCommandDispatchCleanupUtcTicks);
        if (nowTicks - lastCleanup < TimeSpan.FromHours(12).Ticks ||
            Interlocked.CompareExchange(ref _lastCommandDispatchCleanupUtcTicks, nowTicks, lastCleanup) != lastCleanup)
            return;

        await db.TelemetryCommandDispatches
            .Where(x => (x.Status == "Completed" || x.Status == "Failed" || x.Status == "Indeterminate" || x.Status == "Cancelled") &&
                        x.CompletedAt < now.AddDays(-1))
            .ExecuteDeleteAsync(cancellationToken);
    }

    private async Task ExecuteCommandDispatchAsync(Guid dispatchId, CancellationToken cancellationToken)
    {
        TelemetryCommandDispatch? dispatch;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            dispatch = await db.TelemetryCommandDispatches.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == dispatchId && x.Status == "Processing", cancellationToken);
            if (dispatch == null) return;

            var stillOwned = await db.MachineConnectionStates.AsNoTracking().AnyAsync(
                x => x.MachineId == dispatch.MachineId && x.CompanyId == dispatch.CompanyId &&
                     x.IsOnline && x.OwnerNodeId == NodeId,
                cancellationToken);
            if (!stillOwned)
            {
                if (dispatch.Operation == "MdbStatus")
                    await CompleteStatusDispatchAsync(dispatchId,
                        new DeviceStatusResult(false, 0, null, null, null, "machine_offline"), "Failed", cancellationToken);
                else
                    await CompleteCommandDispatchAsync(dispatchId,
                        new DeviceCommandResult(false, 0, 0, "owner_changed_before_send"), "Failed", cancellationToken);
                return;
            }
        }

        try
        {
            string? serial;
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                serial = await db.Machines.AsNoTracking()
                    .Where(x => x.Id == dispatch.MachineId)
                    .Select(x => x.SerialNumber)
                    .FirstOrDefaultAsync(cancellationToken);
            }
            if (serial == null || !_devices.TryGetValue(NormalizeSerial(serial), out var connection))
            {
                if (dispatch.Operation == "MdbStatus")
                    await CompleteStatusDispatchAsync(dispatchId,
                        new DeviceStatusResult(false, 0, null, null, null, "machine_offline"), "Failed", cancellationToken);
                else
                    await CompleteCommandDispatchAsync(dispatchId,
                        new DeviceCommandResult(false, 0, 0, "owner_socket_unavailable"), "Failed", cancellationToken);
                return;
            }

            if (dispatch.Operation == "MdbStatus")
            {
                var result = await SendStatusRequestLocallyAsync(
                    connection,
                    TimeSpan.FromMilliseconds(Math.Clamp(dispatch.TimeoutMilliseconds, 1000, 30000)),
                    cancellationToken);
                await CompleteStatusDispatchAsync(dispatchId, result,
                    result.Success ? "Completed" : "Failed", cancellationToken);
            }
            else
            {
                using var payloadDocument = JsonDocument.Parse(dispatch.PayloadJson);
                var result = await SendCommandLocallyAsync(connection, payloadDocument.RootElement.Clone(), cancellationToken);
                await CompleteCommandDispatchAsync(dispatchId, result,
                    result.Error == "command_outcome_unknown" ? "Indeterminate" : result.Success ? "Completed" : "Failed",
                    cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Keep Processing durable on shutdown. A later requester sees an uncertain
            // outcome; this node never replays a command whose send may have started.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Comando MDB {DispatchId} teve resultado incerto; não será reenviado.", dispatchId);
            if (dispatch?.Operation == "MdbStatus")
                await CompleteStatusDispatchAsync(dispatchId,
                    new DeviceStatusResult(false, 0, null, null, null, "response_timeout"), "Indeterminate", cancellationToken);
            else
                await CompleteCommandDispatchAsync(dispatchId,
                    new DeviceCommandResult(false, 0, 0, "command_outcome_unknown"), "Indeterminate", cancellationToken);
        }
    }

    private async Task CompleteStatusDispatchAsync(
        Guid dispatchId,
        DeviceStatusResult result,
        string status,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.TelemetryCommandDispatches
            .Where(x => x.Id == dispatchId && x.Status == "Processing")
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, status)
                .SetProperty(x => x.CompletedAt, DateTime.UtcNow)
                .SetProperty(x => x.MessageId, result.MessageId == 0 ? (int?)null : result.MessageId)
                .SetProperty(x => x.Success, result.Success)
                .SetProperty(x => x.Error, result.Error)
                .SetProperty(x => x.ResponseStatus, result.Status)
                .SetProperty(x => x.ResponseDataJson, result.DataJson)
                .SetProperty(x => x.ResponseAt, result.ResponseAt), cancellationToken);
    }

    private async Task CompleteCommandDispatchAsync(
        Guid dispatchId,
        DeviceCommandResult result,
        string status,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.TelemetryCommandDispatches
            .Where(x => x.Id == dispatchId && x.Status == "Processing")
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, status)
                .SetProperty(x => x.CompletedAt, DateTime.UtcNow)
                .SetProperty(x => x.MessageId, result.MessageId == 0 ? (int?)null : result.MessageId)
                .SetProperty(x => x.Attempts, result.Attempts)
                .SetProperty(x => x.Success, result.Success)
                .SetProperty(x => x.Error, result.Error), cancellationToken);
    }

    private async Task ProcessInboundAsync(int workerNumber, CancellationToken cancellationToken)
    {
        var workerName = BackgroundWorkerNames.TelemetryInboxConsumer(workerNumber);
        _health?.ReportStarted(workerName);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var processor = scope.ServiceProvider.GetRequiredService<DeviceMessageInboxProcessor>();
                    var didWork = await processor.ProcessNextAsync(cancellationToken);
                    _health?.ReportSuccess(workerName);
                    if (!didWork)
                        await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    _health?.ReportFailure(workerName, ex);
                    _logger.LogError(ex, "Falha no worker de processamento da inbox MDB.");
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                }
            }
        }
        finally { _health?.ReportStopped(workerName); }
    }

    private async Task<int> CountUnfinishedInboxMessagesAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<DeviceMessageInboxProcessor>();
        return await processor.CountUnfinishedAsync(cancellationToken);
    }

    private async Task MonitorHeartbeatAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            _health?.ReportSuccess(BackgroundWorkerNames.Telemetry);
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
                               commandElement.ValueKind == JsonValueKind.String &&
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

        // A resposta de MDB_STATUS pode reutilizar o id gerado pelo servidor. Ela não
        // participa da deduplicação dos eventos iniciados pelo dispositivo, pois os dois
        // lados podem possuir sequências de ids independentes.
        if (isStatusResponse && !connection.ReceivedStatusIds.TryAdd(id, 0))
        {
            await QueuePriorityAsync(connection, new { id, target = connection.Serial, type = "ack", data = "OK" }, cancellationToken);
            return;
        }
        if (isStatusResponse && connection.ReceivedStatusIds.Count > 512)
        {
            foreach (var oldId in connection.ReceivedStatusIds.Keys.OrderBy(x => x).Take(128))
                connection.ReceivedStatusIds.TryRemove(oldId, out _);
        }
        if (!isStatusResponse && !connection.ReceivedIds.TryAdd(id, 0))
        {
            await QueuePriorityAsync(connection, new { id, target = connection.Serial, type = "ack", data = "OK" }, cancellationToken);
            return;
        }
        if (!isStatusResponse && connection.ReceivedIds.Count > 512)
        {
            foreach (var oldId in connection.ReceivedIds.Keys.OrderBy(x => x).Take(128))
                connection.ReceivedIds.TryRemove(oldId, out _);
        }

        var receivedAt = DateTime.UtcNow;
        if (receivedAt <= connection.LastInboundReceivedAt)
            receivedAt = connection.LastInboundReceivedAt.AddTicks(10);
        connection.LastInboundReceivedAt = receivedAt;

        var inbound = new InboundDeviceMessage(
            connection.MachineId,
            connection.CompanyId,
            connection.Serial,
            connection.ConnectionId,
            id,
            data.GetRawText(),
            receivedAt);
        using (var scope = _scopeFactory.CreateScope())
        {
            var inbox = scope.ServiceProvider.GetRequiredService<DeviceMessageInboxService>();
            await inbox.PersistBeforeAcknowledgementAsync(inbound, cancellationToken);
        }
        await QueuePriorityAsync(connection, new { id, target = connection.Serial, type = "ack", data = "OK" }, cancellationToken);
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
        await QueueOutboundAsync(connection, connection.Priority, JsonSerializer.Serialize(payload, JsonOptions), cancellationToken);

    private static async Task QueueOutboundAsync(
        DeviceConnection connection,
        Channel<string> channel,
        string json,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await connection.OutboundGate.WaitAsync(cancellationToken);
            try
            {
                if (connection.CloseStarted)
                    throw new OperationCanceledException("A conexão está encerrando.", cancellationToken);
                if (channel.Writer.TryWrite(json)) return;
            }
            finally
            {
                connection.OutboundGate.Release();
            }

            if (!await channel.Writer.WaitToWriteAsync(cancellationToken))
                throw new ChannelClosedException();
        }
    }

    private static async Task WriteLoopAsync(DeviceConnection connection, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && connection.Socket.State == WebSocketState.Open)
        {
            if (connection.Priority.Reader.TryRead(out var json) || connection.Normal.Reader.TryRead(out json))
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                await connection.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
                continue;
            }

            var closing = false;
            await connection.OutboundGate.WaitAsync(cancellationToken);
            try
            {
                if (connection.Priority.Reader.TryRead(out json) || connection.Normal.Reader.TryRead(out json))
                {
                    // The message is sent after releasing the queue coordination gate.
                }
                else if (connection.DrainRequested)
                {
                    connection.CloseStarted = true;
                    closing = true;
                }
                else
                {
                    json = null;
                }
            }
            finally
            {
                connection.OutboundGate.Release();
            }

            if (json != null)
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                await connection.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
                continue;
            }

            if (closing)
            {
                try
                {
                    await connection.Socket.CloseOutputAsync(
                        WebSocketCloseStatus.EndpointUnavailable,
                        "Servidor em atualização. Reconecte em alguns instantes.",
                        cancellationToken);
                    connection.CloseSent.TrySetResult();
                }
                catch (Exception ex)
                {
                    connection.CloseSent.TrySetException(ex);
                    throw;
                }
                return;
            }

            var highReady = connection.Priority.Reader.WaitToReadAsync(cancellationToken).AsTask();
            var normalReady = connection.Normal.Reader.WaitToReadAsync(cancellationToken).AsTask();
            await Task.WhenAny(highReady, normalReady, connection.DrainSignal.Task);
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
        else if (state.CompanyId != connection.CompanyId)
        {
            _logger.LogWarning(
                "Estado MDB da máquina {MachineId} estava associado a outra empresa; a reconexão autenticada corrigiu o vínculo.",
                connection.MachineId);
            state.CompanyId = connection.CompanyId;
            state.MonitoringEnabled = false;
            state.DisconnectedAt = null;
            state.ActivatedAt = null;
            state.ActivatedByUserId = null;
            state.DeactivatedAt = null;
            state.DeactivatedByUserId = null;
            state.MdbStatus = null;
            state.MdbStatusRaw = null;
            state.MdbStatusUpdatedAt = null;
            state.AutoOpenLastAttemptAt = null;
            state.AutoOpenLastError = null;
        }
        state.IsOnline = true;
        state.ConnectionId = connection.ConnectionId;
        state.OwnerNodeId = NodeId;
        state.OwnerConnectionId = connection.ConnectionId;
        state.ConnectedAt = DateTime.UtcNow;
        state.LastSeenAt = DateTime.UtcNow;
        state.MdbStatusRequestAt = null;
        state.MdbStatusRequestMessageId = null;
        db.TelemetryEvents.Add(new TelemetryEvent
        {
            CompanyId = connection.CompanyId,
            MachineId = connection.MachineId,
            EventType = "device.connected"
        });
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
            var state = await db.MachineConnectionStates.FirstOrDefaultAsync(
                x => x.MachineId == connection.MachineId && x.CompanyId == connection.CompanyId);
            var disconnectedCurrentOwner = false;
            if (state != null && state.ConnectionId == connection.ConnectionId)
            {
                state.IsOnline = false;
                state.ConnectionId = null;
                state.OwnerNodeId = null;
                state.OwnerConnectionId = null;
                state.DisconnectedAt = DateTime.UtcNow;
                disconnectedCurrentOwner = true;
                db.TelemetryEvents.Add(new TelemetryEvent
                {
                    CompanyId = connection.CompanyId,
                    MachineId = connection.MachineId,
                    EventType = "device.disconnected"
                });
                await db.SaveChangesAsync();
            }
            if (disconnectedCurrentOwner)
            {
                await _panelHub.BroadcastCompanyAsync(connection.CompanyId, new
                {
                    type = "connection.updated", machineId = connection.MachineId, online = false,
                    disconnectedAt = state!.DisconnectedAt
                });
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Falha ao persistir desconexão de {Serial}.", connection.Serial); }
    }

    private async Task PersistLastSeenAsync(DeviceConnection connection)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var state = await db.MachineConnectionStates.FirstOrDefaultAsync(
                x => x.MachineId == connection.MachineId && x.CompanyId == connection.CompanyId);
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
            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (socket.State == WebSocketState.CloseReceived)
                {
                    await socket.CloseOutputAsync(
                        socket.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                        socket.CloseStatusDescription,
                        CancellationToken.None);
                }
                return null;
            }
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

    private bool TryRegisterDevice(DeviceConnection connection)
    {
        lock (_shutdownLock)
        {
            if (_isStopping) return false;
            if (_devices.TryGetValue(connection.Serial, out var previous))
            {
                // In legacy mode the handshake proves only knowledge of a serial,
                // which is not enough to evict a live machine connection. Keep the
                // current socket until it closes; an authenticated reconnect may
                // replace it after device credentials have been provisioned.
                if (!_requireDeviceCredentials && previous.Socket.State == WebSocketState.Open)
                {
                    _logger.LogWarning(
                        "Conexão duplicada rejeitada para {Serial}; a conexão ativa foi preservada enquanto credenciais de dispositivo não são obrigatórias.",
                        connection.Serial);
                    return false;
                }

                previous.Cts.Cancel();
                try { previous.Socket.Abort(); } catch { }
            }
            _devices[connection.Serial] = connection;
            return true;
        }
    }

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
        public CancellationTokenSource WriterCts { get; } = new();
        public Task? WriterTask { get; set; }
        public TaskCompletionSource Disconnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DrainSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CloseSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public SemaphoreSlim CommandGate { get; } = new(1, 1);
        public SemaphoreSlim InboundGate { get; } = new(1, 1);
        public SemaphoreSlim OutboundGate { get; } = new(1, 1);
        public ConcurrentDictionary<int, TaskCompletionSource<bool>> PendingAcks { get; } = new();
        public PendingStatusRequest? PendingStatusRequest;
        public ConcurrentDictionary<int, byte> ReceivedIds { get; } = new();
        public ConcurrentDictionary<int, byte> ReceivedStatusIds { get; } = new();
        public Channel<string> Priority { get; } = Channel.CreateBounded<string>(1000);
        public Channel<string> Normal { get; } = Channel.CreateBounded<string>(1000);
        public bool DrainRequested { get; set; }
        public bool CloseStarted { get; set; }
        public int LastServerMessageId;
        public int Signal;
        public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
        public DateTime LastPersistedSeenAt { get; set; } = DateTime.MinValue;
        public DateTime LastInboundReceivedAt { get; set; } = DateTime.MinValue;
    }

    private sealed record PendingStatusRequest(
        int MessageId,
        TaskCompletionSource<InboundStatusResponse> Completion);

    private sealed record InboundStatusResponse(
        string? Status,
        string DataJson,
        DateTime ReceivedAt);
}
