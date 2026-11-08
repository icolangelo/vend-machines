using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
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
    DateTime ReceivedAt,
    string? OwnerNodeId = null);

public sealed record DeviceCommandResult(bool Success, int MessageId, int Attempts, string? Error = null);

public sealed record DeviceStatusResult(
    bool Success,
    int MessageId,
    string? Status,
    string? DataJson,
    DateTime? ResponseAt,
    string? Error = null);

public sealed class TelemetryConnectionManager : BackgroundService, ITelemetryConnectionOperations
{
    public const int CurrentCommandRoutingVersion = 1;
    private const int WebSocketCloseTimeoutSeconds = 2;
    private static readonly TimeSpan CommandQueueWaitTimeout = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static long _lastCommandDispatchCleanupUtcTicks;
    private static long _lastStaleStatusDispatchCleanupUtcTicks;
    private const int OfflinePersistenceMaxAttempts = 4;
    private readonly ConcurrentDictionary<string, DeviceConnection> _devices = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TelemetryPanelHub _panelHub;
    private readonly ILogger<TelemetryConnectionManager> _logger;
    private readonly BackgroundWorkerHealthRegistry? _health;
    private readonly ShutdownWriteRequestGate? _shutdownWriteRequestGate;
    private readonly TimeSpan _applicationStoppingDrainTimeout;
    public string NodeId { get; } = Guid.NewGuid().ToString("N");
    private readonly bool _requireDeviceCredentials;
    private readonly object _shutdownLock = new();
    private readonly CancellationTokenSource _inboundWorkerStop = new();
    private Task[] _inboundWorkers = Array.Empty<Task>();
    private Task? _drainTask;
    private int _activeSessionStarts;
    private TaskCompletionSource _sessionStartsDrained = CompletedSessionStarts();
    private volatile bool _isStopping;

    public TelemetryConnectionManager(
        IServiceScopeFactory scopeFactory,
        TelemetryPanelHub panelHub,
        ILogger<TelemetryConnectionManager> logger,
        IConfiguration? configuration = null,
        IHostApplicationLifetime? lifetime = null,
        BackgroundWorkerHealthRegistry? health = null,
        ShutdownWriteRequestGate? shutdownWriteRequestGate = null)
    {
        _scopeFactory = scopeFactory;
        _panelHub = panelHub;
        _logger = logger;
        _health = health;
        _shutdownWriteRequestGate = shutdownWriteRequestGate;
        _requireDeviceCredentials = configuration?.GetValue<bool>("Telemetry:RequireDeviceCredentials") == true;
        var shutdownDrainTimeoutSeconds = configuration?.GetValue<int?>("Telemetry:ApplicationStoppingDrainTimeoutSeconds")
            ?? GracefulShutdownBudget.DefaultTelemetryDrainTimeoutSeconds;
        _applicationStoppingDrainTimeout = GracefulShutdownBudget.GetTelemetryDrainTimeout(shutdownDrainTimeoutSeconds);
        lifetime?.ApplicationStopping.Register(() =>
        {
            try
            {
                _logger.LogInformation("Iniciando drenagem de telemetria antes do encerramento do servidor.");
                // ApplicationStopping callbacks run before Kestrel stops active requests.
                // Wait here so long-lived WebSocket requests remain alive during the drain.
                using var drainDeadline = new CancellationTokenSource(_applicationStoppingDrainTimeout);
                StartDrain(drainDeadline.Token).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha na drenagem de telemetria durante o encerramento.");
            }
        });
    }

    public bool IsOnline(string serial) =>
        _devices.TryGetValue(NormalizeSerial(serial), out var connection) && connection.Socket.State == WebSocketState.Open;

    public bool IsLocalConnectionOnline(string serial, string? expectedConnectionId) =>
        !string.IsNullOrWhiteSpace(expectedConnectionId) &&
        _devices.TryGetValue(NormalizeSerial(serial), out var connection) &&
        connection.Socket.State == WebSocketState.Open &&
        string.Equals(connection.ConnectionId, expectedConnectionId, StringComparison.Ordinal);

    public IReadOnlyDictionary<string, string> GetActiveMachineConnectionsForLease() =>
        _devices.Values
            .Where(connection => connection.Socket.State == WebSocketState.Open)
            .GroupBy(connection => connection.MachineId, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.First().ConnectionId, StringComparer.Ordinal);

    public IDisposable? TryBeginSessionStart()
    {
        lock (_shutdownLock)
        {
            if (_isStopping) return null;
            if (_activeSessionStarts++ == 0)
                _sessionStartsDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return new SessionStartAdmission(this);
        }
    }

    public async Task DisconnectAsync(string serial, string reason)
    {
        if (!_devices.TryRemove(NormalizeSerial(serial), out var connection)) return;
        await CloseConnectionAsync(serial, connection, reason);
    }

    private async Task CloseConnectionAsync(string serial, DeviceConnection connection, string reason)
    {
        connection.Cts.Cancel();
        connection.Priority.Writer.TryComplete();
        connection.Normal.Writer.TryComplete();
        try
        {
            if (connection.Socket.State == WebSocketState.Open)
            {
                using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(WebSocketCloseTimeoutSeconds));
                await connection.Socket.CloseAsync(
                    WebSocketCloseStatus.PolicyViolation,
                    reason,
                    closeTimeout.Token);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or InvalidOperationException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "Não foi possível fechar a conexão revogada da máquina {Serial}.", serial);
        }
        finally
        {
            try { connection.Socket.Abort(); } catch { }
            await MarkOfflineAsync(connection);
        }
    }

    private async Task<bool> DisconnectConnectionAsync(string normalizedSerial, DeviceConnection expectedConnection, string reason)
    {
        var current = new KeyValuePair<string, DeviceConnection>(normalizedSerial, expectedConnection);
        if (!((ICollection<KeyValuePair<string, DeviceConnection>>)_devices).Remove(current))
            return false;

        await CloseConnectionAsync(normalizedSerial, expectedConnection, reason);
        return true;
    }

    public async Task<DeviceCommandResult> DisconnectMachineAsync(
        string serial,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (_isStopping)
            return new DeviceCommandResult(false, 0, 0, "server_restarting");

        var normalizedSerial = NormalizeSerial(serial);
        if (_devices.TryGetValue(normalizedSerial, out var connection) && connection.Socket.State == WebSocketState.Open)
        {
            var disconnected = await DisconnectConnectionAsync(normalizedSerial, connection, reason);
            return disconnected
                ? new DeviceCommandResult(true, 0, 0)
                : new DeviceCommandResult(false, 0, 0, "command_outcome_unknown");
        }

        return await EnqueueRemoteDisconnectAsync(normalizedSerial, reason, cancellationToken);
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
        _shutdownWriteRequestGate?.StopAccepting();
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
        using var admissionTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        admissionTimeout.CancelAfter(GracefulShutdownBudget.GetSessionAdmissionTimeout(_applicationStoppingDrainTimeout));
        try
        {
            var writeRequestsDrained = _shutdownWriteRequestGate?.WaitForDrainAsync(admissionTimeout.Token)
                ?? Task.CompletedTask;
            await Task.WhenAll(writeRequestsDrained, WaitForSessionStartsAsync(admissionTimeout.Token));
        }
        catch (OperationCanceledException) when (admissionTimeout.IsCancellationRequested)
        {
            _logger.LogCritical(
                "O prazo para concluir gravações e aberturas de sessão expirou. Gravações HTTP ainda ativas: {ActiveWriteRequests}; o servidor seguirá para drenagem dos sockets.",
                _shutdownWriteRequestGate?.ActiveRequestCount ?? 0);
        }

        using var drainTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // A server command can wait for up to three five-second ACK attempts. Leave
        // a separate bounded window for that operation and the socket/inbox drain.
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
                using var countTimeout = new CancellationTokenSource(
                    TimeSpan.FromSeconds(GracefulShutdownBudget.TelemetryDiagnosticTimeoutSeconds));
                var pending = await CountUnfinishedInboxMessagesAsync(countTimeout.Token);
                _logger.LogCritical(
                    "O prazo de drenagem terminou. Mensagens MDB persistidas ainda pendentes: {PendingCount}; gravações HTTP ainda ativas: {ActiveWriteRequests}; as mensagens persistidas serão retomadas na próxima inicialização.",
                    pending,
                    _shutdownWriteRequestGate?.ActiveRequestCount ?? 0);
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
            try
            {
                await Task.WhenAll(Volatile.Read(ref _inboundWorkers)).WaitAsync(
                    TimeSpan.FromSeconds(GracefulShutdownBudget.TelemetryWorkerStopTimeoutSeconds));
            }
            catch (OperationCanceledException) { }
            catch (TimeoutException)
            {
                _logger.LogCritical("Workers MDB ainda ativos após o prazo final de encerramento; as mensagens não confirmadas permanecem na inbox durável.");
            }
            catch (Exception ex) { _logger.LogError(ex, "Worker de inbox encerrou com erro durante a drenagem."); }
        }
    }

    private Task WaitForSessionStartsAsync(CancellationToken cancellationToken)
    {
        Task drained;
        lock (_shutdownLock) drained = _sessionStartsDrained.Task;
        return drained.WaitAsync(cancellationToken);
    }

    private void EndSessionStart()
    {
        lock (_shutdownLock)
        {
            if (--_activeSessionStarts == 0)
                _sessionStartsDrained.TrySetResult();
        }
    }

    private static TaskCompletionSource CompletedSessionStarts()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        completed.SetResult();
        return completed;
    }

    private sealed class SessionStartAdmission(TelemetryConnectionManager owner) : IDisposable
    {
        private TelemetryConnectionManager? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndSessionStart();
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

        var disconnectedStateHandled = false;
        try
        {
            disconnectedStateHandled = await connection.Disconnected.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                cancellationToken);
        }
        catch (TimeoutException) { }
        catch (OperationCanceledException) { }

        if (disconnectedStateHandled) return;

        connection.Cts.Cancel();
        try { connection.Socket.Abort(); } catch { }
        await MarkOfflineAsync(connection, cancellationToken);
    }

    public async Task HandleDeviceAsync(WebSocket socket, CancellationToken requestAborted)
    {
        DeviceConnection? connection = null;
        CancellationTokenSource? connectionLinked = null;
        CancellationTokenSource? writerLinked = null;
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
                await CloseRejectedSocketAsync(
                    socket,
                    WebSocketCloseStatus.PolicyViolation,
                    "A primeira mensagem deve ser start com target.",
                    handshakeTimeout.Token);
                return;
            }

            var serial = NormalizeSerial(target.GetString()!);
            var credential = first.TryGetProperty("credential", out var credentialElement) &&
                credentialElement.ValueKind == JsonValueKind.String
                    ? credentialElement.GetString()
                    : null;
            var startId = first.TryGetProperty("id", out var startIdElement) && startIdElement.TryGetInt32(out var parsedStartId)
                ? parsedStartId : 0;

            using (var admissionScope = _scopeFactory.CreateScope())
            {
                var admissionDb = admissionScope.ServiceProvider.GetRequiredService<AppDbContext>();
                await using var admissionTransaction = await admissionDb.Database.BeginTransactionAsync(handshakeTimeout.Token);
                var machineCandidate = await admissionDb.Machines.AsNoTracking().FirstOrDefaultAsync(
                    candidate => candidate.NormalizedSerialNumber == serial,
                    handshakeTimeout.Token);
                if (machineCandidate == null || !machineCandidate.CompanyId.HasValue ||
                    !await MachineOperationLock.AcquireAsync(
                        admissionDb,
                        machineCandidate.Id,
                        machineCandidate.CompanyId.Value,
                        handshakeTimeout.Token))
                {
                    _logger.LogWarning("Handshake MDB rejeitado: código {RejectionCode}.", "unknown_machine");
                    await CloseRejectedSocketAsync(
                        socket,
                        WebSocketCloseStatus.PolicyViolation,
                        "Máquina não cadastrada.",
                        handshakeTimeout.Token);
                    return;
                }

                var machine = await admissionDb.Machines.FirstOrDefaultAsync(
                    candidate => candidate.Id == machineCandidate.Id && candidate.CompanyId == machineCandidate.CompanyId,
                    handshakeTimeout.Token);
                if (machine == null || machine.NormalizedSerialNumber != serial)
                {
                    _logger.LogWarning("Handshake MDB rejeitado: código {RejectionCode}.", "unknown_machine");
                    await CloseRejectedSocketAsync(
                        socket,
                        WebSocketCloseStatus.PolicyViolation,
                        "Máquina não cadastrada.",
                        handshakeTimeout.Token);
                    return;
                }

                if (!machine.IsActive)
                {
                    _logger.LogWarning("Handshake MDB rejeitado: código {RejectionCode} para máquina {MachineId}.", "inactive_machine", machine.Id);
                    await CloseRejectedSocketAsync(
                        socket,
                        WebSocketCloseStatus.PolicyViolation,
                        "Máquina desativada.",
                        handshakeTimeout.Token);
                    return;
                }

                var authenticatedDevice = false;
                if (_requireDeviceCredentials || credential is not null)
                {
                    authenticatedDevice = DeviceCredentialService.Verify(
                        credential ?? string.Empty,
                        machine.TelemetryCredentialHash,
                        machine.TelemetryCredentialRevokedAt);
                    if (!authenticatedDevice)
                    {
                        _logger.LogWarning("Credencial de telemetria rejeitada para a máquina {MachineId}.", machine.Id);
                        await CloseRejectedSocketAsync(
                            socket,
                            WebSocketCloseStatus.PolicyViolation,
                            "Credencial de telemetria inválida.",
                            handshakeTimeout.Token);
                        return;
                    }
                }

                var newConnection = new DeviceConnection(machine.Id, machine.CompanyId!.Value, serial, socket, authenticatedDevice);
                if (!TryRegisterDevice(newConnection))
                {
                    await CloseRejectedSocketAsync(
                        socket,
                        WebSocketCloseStatus.EndpointUnavailable,
                        "Máquina já conectada ou servidor em atualização. Reconecte em alguns instantes.",
                        handshakeTimeout.Token);
                    return;
                }
                connection = newConnection;
                connectionLinked = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, connection.Cts.Token);
                writerLinked = CancellationTokenSource.CreateLinkedTokenSource(connectionLinked.Token, connection.WriterCts.Token);
                connection.WriterTask = WriteLoopAsync(connection, writerLinked.Token);
                connection.ConnectedAt = DateTime.UtcNow;
                await MarkOnlineAsync(admissionDb, connection, connectionLinked.Token);
                await admissionTransaction.CommitAsync(connectionLinked.Token);
            }

            await QueuePriorityAsync(connection, new { id = startId, target = serial, type = "ack", data = "OK" }, connectionLinked!.Token);
            await BroadcastDeviceOnlineAsync(connection);

            while (socket.State == WebSocketState.Open && connectionLinked is { IsCancellationRequested: false })
            {
                var text = await ReadTextAsync(socket, connectionLinked.Token);
                if (text == null) break;
                await connection.InboundGate.WaitAsync(connectionLinked.Token);
                try
                {
                    await HandleIncomingAsync(connection, text, connectionLinked.Token);
                }
                finally
                {
                    connection.InboundGate.Release();
                }
            }

            connection.Cts.Cancel();
            connection.Priority.Writer.TryComplete();
            connection.Normal.Writer.TryComplete();
            try
            {
                if (connection.WriterTask is { } writerTask)
                    await writerTask;
            }
            catch { }
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
            if (socket.State == WebSocketState.Open)
                await CloseRejectedSocketAsync(socket, WebSocketCloseStatus.PolicyViolation, "JSON inválido.", requestAborted);
        }
        catch (WebSocketException ex)
        {
            _logger.LogDebug(ex, "Conexão ESP encerrada.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao admitir ou processar a conexão MDB da máquina {MachineId}.", connection?.MachineId ?? "desconhecida");
        }
        finally
        {
            try { socket.Abort(); } catch { }
            if (connection != null)
            {
                connection.Cts.Cancel();
                connection.Priority.Writer.TryComplete();
                connection.Normal.Writer.TryComplete();
                if (connection.WriterTask is { } writerTask)
                {
                    try
                    {
                        await writerTask.WaitAsync(TimeSpan.FromSeconds(WebSocketCloseTimeoutSeconds));
                    }
                    catch (TimeoutException)
                    {
                        connection.WriterCts.Cancel();
                        try { connection.Socket.Abort(); } catch { }
                    }
                    catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or InvalidOperationException)
                    {
                    }
                }
            }
            var disconnectedStateHandled = connection == null;
            if (connection != null && _devices.TryGetValue(connection.Serial, out var current) && ReferenceEquals(current, connection))
            {
                _devices.TryRemove(connection.Serial, out _);
                using var shutdownCleanupTimeout = _isStopping
                    ? new CancellationTokenSource(TimeSpan.FromSeconds(GracefulShutdownBudget.TelemetryDiagnosticTimeoutSeconds))
                    : null;
                disconnectedStateHandled = await MarkOfflineAsync(
                    connection,
                    shutdownCleanupTimeout?.Token ?? CancellationToken.None);
            }
            else if (connection != null)
            {
                // Another connection replaced this socket; its persisted owner must remain untouched.
                disconnectedStateHandled = true;
            }
            connection?.Disconnected.TrySetResult(disconnectedStateHandled);
            writerLinked?.Dispose();
            connectionLinked?.Dispose();
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

        if (!await connection.CommandGate.WaitAsync(CommandQueueWaitTimeout, cancellationToken))
            return new DeviceCommandResult(false, 0, 0, "command_queue_timeout");
        var commandId = 0;
        try
        {
            if (_isStopping)
                return new DeviceCommandResult(false, 0, 0, "server_restarting");
            if (!await IsCurrentLocalOwnerAsync(connection, cancellationToken))
                return new DeviceCommandResult(false, 0, 0, "machine_owner_unknown");

            var id = Interlocked.Increment(ref connection.LastServerMessageId);
            commandId = id;
            var json = JsonSerializer.Serialize(new { id, target = connection.Serial, type = "msg", data }, JsonOptions);
            var acknowledgement = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.PendingAcks[id] = acknowledgement;

            for (var attempt = 1; attempt <= 3; attempt++)
            {
                if (attempt > 1 && !await IsCurrentLocalOwnerAsync(connection, cancellationToken))
                {
                    _logger.LogWarning(
                        "A propriedade do socket MDB mudou após a tentativa {Attempt} do comando {MessageId} para {Serial}; nenhuma nova tentativa será enviada.",
                        attempt - 1,
                        id,
                        connection.Serial);
                    return new DeviceCommandResult(false, id, attempt - 1, "command_outcome_unknown");
                }

                try
                {
                    await QueueOutboundAsync(connection, connection.Normal, json, cancellationToken);
                    await acknowledgement.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                    return new DeviceCommandResult(true, id, attempt);
                }
                catch (TimeoutException) when (attempt < 3) { }
                catch (TimeoutException)
                {
                    return new DeviceCommandResult(false, id, attempt, "ack_timeout");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(
                        "Comando MDB {MessageId} para {Serial} foi cancelado após entrar na fila de envio; resultado incerto.",
                        id,
                        connection.Serial);
                    return new DeviceCommandResult(false, id, attempt, "command_outcome_unknown");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Falha ao enviar o comando MDB {MessageId} para {Serial}; resultado incerto.", id, connection.Serial);
                    return new DeviceCommandResult(false, id, attempt, "command_outcome_unknown");
                }
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
            .Select(x => new { x.IsOnline, x.LastSeenAt, x.OwnerNodeId, x.CommandRoutingVersion })
            .FirstOrDefaultAsync(cancellationToken);
        if (route?.IsOnline != true || !route.LastSeenAt.HasValue || route.LastSeenAt < DateTime.UtcNow - MachineConnectionFreshness.Window)
            return new DeviceCommandResult(false, 0, 0, "machine_offline");
        if (string.IsNullOrWhiteSpace(route.OwnerNodeId))
            return new DeviceCommandResult(false, 0, 0, "machine_owner_unknown");
        if (route.CommandRoutingVersion != CurrentCommandRoutingVersion)
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CancelPendingDispatchAfterCancellationAsync(dispatch.Id, "dispatch_cancelled_before_send");
            throw;
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
                if (current.Status is "Completed" or "Failed" or "Indeterminate" or "Cancelled")
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CancelPendingDispatchAfterCancellationAsync(dispatch.Id, "dispatch_cancelled_before_send");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Acompanhamento do comando remoto {DispatchId} falhou após registro; resultado incerto.", dispatch.Id);
            return new DeviceCommandResult(false, 0, 0, "command_outcome_unknown");
        }
    }

    private async Task CancelPendingDispatchAfterCancellationAsync(Guid dispatchId, string reason)
    {
        try
        {
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.TelemetryCommandDispatches
                .Where(dispatch => dispatch.Id == dispatchId && dispatch.Status == "Pending")
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(dispatch => dispatch.Status, "Cancelled")
                    .SetProperty(dispatch => dispatch.CompletedAt, DateTime.UtcNow)
                    .SetProperty(dispatch => dispatch.Error, reason), cleanupTimeout.Token);
        }
        catch (Exception cleanupException)
        {
            _logger.LogError(
                "Não foi possível cancelar o despacho MDB {DispatchId} após o cancelamento do chamador; se já estava em processamento, o resultado permanece incerto. Tipo de erro: {ExceptionType}.",
                dispatchId,
                cleanupException.GetType().Name);
        }
    }

    private async Task<DeviceCommandResult> EnqueueRemoteDisconnectAsync(
        string normalizedSerial,
        string reason,
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
            .Select(x => new { x.IsOnline, x.OwnerNodeId, x.OwnerConnectionId, x.CommandRoutingVersion })
            .FirstOrDefaultAsync(cancellationToken);
        if (route == null)
            return new DeviceCommandResult(true, 0, 0);
        if (!route.IsOnline)
            return new DeviceCommandResult(true, 0, 0);
        if (string.IsNullOrWhiteSpace(route.OwnerNodeId) || string.IsNullOrWhiteSpace(route.OwnerConnectionId) ||
            route.CommandRoutingVersion != CurrentCommandRoutingVersion)
            return new DeviceCommandResult(false, 0, 0, "machine_owner_unknown");
        if (route.OwnerNodeId == NodeId)
            return new DeviceCommandResult(false, 0, 0, "owner_socket_unavailable");

        var dispatch = new TelemetryCommandDispatch
        {
            CompanyId = machine.CompanyId,
            MachineId = machine.Id,
            OwnerNodeId = route.OwnerNodeId,
            Operation = "Disconnect",
            TimeoutMilliseconds = 3000,
            PayloadJson = JsonSerializer.Serialize(new { reason, ownerConnectionId = route.OwnerConnectionId }, JsonOptions),
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
            _logger.LogError(ex, "Não foi possível registrar a desconexão MDB para {Serial}.", normalizedSerial);
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
                    .Select(x => new { x.Status, x.Success, x.Error })
                    .FirstOrDefaultAsync(cancellationToken);
                if (current == null)
                    return new DeviceCommandResult(false, 0, 0, "command_outcome_unknown");
                if (current.Status is "Completed" or "Failed" or "Indeterminate" or "Cancelled")
                    return new DeviceCommandResult(current.Success == true, 0, 0, current.Error);

                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
            }

            // A not-yet-claimed disconnect must not execute later after the caller rolls
            // back the credential change. Compete atomically with the owner's claim.
            var cancelled = await db.TelemetryCommandDispatches
                .Where(x => x.Id == dispatch.Id && x.Status == "Pending")
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, "Cancelled")
                    .SetProperty(x => x.CompletedAt, DateTime.UtcNow)
                    .SetProperty(x => x.Error, "dispatch_timeout_before_send"), cancellationToken);
            return cancelled > 0
                ? new DeviceCommandResult(false, 0, 0, "dispatch_timeout_before_send")
                : new DeviceCommandResult(false, 0, 0, "command_outcome_unknown");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Preserve cancellation semantics for the caller while preventing a
            // disconnect that is still queued from executing after that caller
            // has abandoned the credential change.
            try
            {
                await db.TelemetryCommandDispatches
                    .Where(x => x.Id == dispatch.Id && x.Status == "Pending")
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.Status, "Cancelled")
                        .SetProperty(x => x.CompletedAt, DateTime.UtcNow)
                        .SetProperty(x => x.Error, "dispatch_cancelled_before_send"), CancellationToken.None);
            }
            catch (Exception cancellationCleanupException)
            {
                _logger.LogError(cancellationCleanupException,
                    "Não foi possível cancelar a desconexão MDB {DispatchId} após o cancelamento do chamador.", dispatch.Id);
            }
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Acompanhamento da desconexão MDB {DispatchId} falhou após registro.", dispatch.Id);
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
            .Select(x => new { x.IsOnline, x.LastSeenAt, x.OwnerNodeId, x.CommandRoutingVersion })
            .FirstOrDefaultAsync(cancellationToken);
        if (route?.IsOnline != true || !route.LastSeenAt.HasValue || route.LastSeenAt < DateTime.UtcNow - MachineConnectionFreshness.Window)
            return new DeviceStatusResult(false, 0, null, null, null, "machine_offline");
        if (string.IsNullOrWhiteSpace(route.OwnerNodeId))
            return new DeviceStatusResult(false, 0, null, null, null, "machine_owner_unknown");
        if (route.CommandRoutingVersion != CurrentCommandRoutingVersion)
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CancelPendingDispatchAfterCancellationAsync(dispatch.Id, "status_cancelled_before_send");
            throw;
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
                if (current.Status is "Completed" or "Failed" or "Indeterminate" or "Cancelled")
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CancelPendingDispatchAfterCancellationAsync(dispatch.Id, "status_cancelled_before_send");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Acompanhamento da consulta MDB remota {DispatchId} falhou.", dispatch.Id);
            return new DeviceStatusResult(false, 0, null, null, null, "response_timeout");
        }
    }

    public async Task<bool> IsOnlineAsync(string serial, CancellationToken cancellationToken = default)
    {
        var normalizedSerial = NormalizeSerial(serial);
        if (_devices.TryGetValue(normalizedSerial, out var local) &&
            local.Socket.State == WebSocketState.Open &&
            await IsCurrentLocalOwnerAsync(local, cancellationToken))
            return true;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var freshnessCutoff = DateTime.UtcNow - MachineConnectionFreshness.Window;
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

    public async Task<bool> HasCommandRouteAsync(string serial, CancellationToken cancellationToken = default)
    {
        var normalizedSerial = NormalizeSerial(serial);
        if (_devices.TryGetValue(normalizedSerial, out var local) &&
            local.Socket.State == WebSocketState.Open &&
            await IsCurrentLocalOwnerAsync(local, cancellationToken))
            return true;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var freshnessCutoff = DateTime.UtcNow - MachineConnectionFreshness.Window;
            return await db.Machines.AsNoTracking()
                .Where(machine => machine.NormalizedSerialNumber == normalizedSerial)
                .Join(db.MachineConnectionStates.AsNoTracking(), machine => machine.Id, state => state.MachineId,
                    (machine, state) => new
                    {
                        MachineCompanyId = machine.CompanyId,
                        StateCompanyId = state.CompanyId,
                        state.IsOnline,
                        state.LastSeenAt,
                        state.ConnectionId,
                        state.OwnerNodeId,
                        state.OwnerConnectionId,
                        state.CommandRoutingVersion
                    })
                .AnyAsync(state => state.MachineCompanyId == state.StateCompanyId &&
                                   state.IsOnline && state.LastSeenAt >= freshnessCutoff &&
                                   state.ConnectionId != null && state.ConnectionId != "" &&
                                   state.OwnerNodeId != null && state.OwnerNodeId != "" &&
                                   state.OwnerConnectionId == state.ConnectionId &&
                                   state.CommandRoutingVersion == CurrentCommandRoutingVersion &&
                                   state.OwnerNodeId != NodeId,
                    cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Não foi possível confirmar a rota de comandos da máquina {Serial}.", normalizedSerial);
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
            var freshnessCutoff = DateTime.UtcNow - MachineConnectionFreshness.Window;
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
        if (!await connection.CommandGate.WaitAsync(CommandQueueWaitTimeout, linked.Token))
            return new DeviceStatusResult(false, 0, null, null, null, "command_queue_timeout");
        PendingStatusRequest? pending = null;
        try
        {
            if (_isStopping)
                return new DeviceStatusResult(false, 0, null, null, null, "server_restarting");
            if (!await IsCurrentLocalOwnerAsync(connection, linked.Token))
                return new DeviceStatusResult(false, 0, null, null, null, "machine_owner_unknown");

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
        var workerName = BackgroundWorkerNames.TelemetryCommandDispatch;
        _health?.ReportStarted(workerName);
        try
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
                        .Select(x => new { x.Id, x.Operation })
                        .FirstOrDefaultAsync(cancellationToken);
                    if (candidate == null)
                    {
                        _health?.ReportSuccess(workerName);
                        await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
                        continue;
                    }

                    var processingAt = DateTime.UtcNow;
                    int claimed;
                    try
                    {
                        claimed = await db.TelemetryCommandDispatches
                            .Where(x => x.Id == candidate.Id && x.OwnerNodeId == NodeId && x.Status == "Pending")
                            .ExecuteUpdateAsync(setters => setters
                                .SetProperty(x => x.Status, "Processing")
                                .SetProperty(x => x.ProcessingStartedAt, processingAt), cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        if (candidate.Operation == "MdbStatus")
                            await CancelStatusDispatchAfterShutdownAsync(candidate.Id);
                        throw;
                    }
                    if (claimed > 0)
                    {
                        try
                        {
                            await ExecuteCommandDispatchAsync(candidate.Id, cancellationToken);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            if (candidate.Operation == "MdbStatus")
                                await CancelStatusDispatchAfterShutdownAsync(candidate.Id);
                            throw;
                        }
                    }

                    _health?.ReportSuccess(workerName);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    _health?.ReportFailure(workerName, ex);
                    _logger.LogError(ex, "Falha no roteador durável de comandos MDB do nó {NodeId}.", NodeId);
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                }
            }
        }
        finally { _health?.ReportStopped(workerName); }
    }

    private async Task CancelStatusDispatchAfterShutdownAsync(Guid dispatchId)
    {
        try
        {
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.TelemetryCommandDispatches
                .Where(dispatch => dispatch.Id == dispatchId &&
                    dispatch.Operation == "MdbStatus" &&
                    (dispatch.Status == "Pending" || dispatch.Status == "Processing"))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(dispatch => dispatch.Status, "Cancelled")
                    .SetProperty(dispatch => dispatch.CompletedAt, DateTime.UtcNow)
                    .SetProperty(dispatch => dispatch.Error, "status_cancelled_during_shutdown"), cleanupTimeout.Token);
        }
        catch (Exception cleanupException)
        {
            _logger.LogError(
                "Não foi possível encerrar a consulta MDB de status {DispatchId} durante o shutdown. Tipo de erro: {ExceptionType}.",
                dispatchId,
                cleanupException.GetType().Name);
        }
    }

    private static async Task CleanupCommandDispatchesIfDueAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var nowTicks = now.Ticks;
        var lastStaleStatusCleanup = Volatile.Read(ref _lastStaleStatusDispatchCleanupUtcTicks);
        if (nowTicks - lastStaleStatusCleanup >= TimeSpan.FromMinutes(5).Ticks &&
            Interlocked.CompareExchange(ref _lastStaleStatusDispatchCleanupUtcTicks, nowTicks, lastStaleStatusCleanup) == lastStaleStatusCleanup)
        {
            try
            {
                var staleStatusBefore = now.AddMinutes(-2);
                await db.TelemetryCommandDispatches
                    .Where(dispatch => dispatch.Operation == "MdbStatus" &&
                        ((dispatch.Status == "Pending" && dispatch.CreatedAt < staleStatusBefore) ||
                         (dispatch.Status == "Processing" && dispatch.ProcessingStartedAt < staleStatusBefore)))
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(dispatch => dispatch.Status, "Cancelled")
                        .SetProperty(dispatch => dispatch.CompletedAt, now)
                        .SetProperty(dispatch => dispatch.ProcessingStartedAt, (DateTime?)null)
                        .SetProperty(dispatch => dispatch.Error, "status_request_expired"), cancellationToken);
            }
            catch
            {
                Interlocked.CompareExchange(ref _lastStaleStatusDispatchCleanupUtcTicks, lastStaleStatusCleanup, nowTicks);
                throw;
            }
        }

        var lastCleanup = Volatile.Read(ref _lastCommandDispatchCleanupUtcTicks);
        if (nowTicks - lastCleanup < TimeSpan.FromHours(12).Ticks ||
            Interlocked.CompareExchange(ref _lastCommandDispatchCleanupUtcTicks, nowTicks, lastCleanup) != lastCleanup)
            return;

        await db.TelemetryCommandDispatches
            .Where(x => (x.Status == "Completed" || x.Status == "Failed" || x.Status == "Indeterminate" || x.Status == "Cancelled") &&
                        x.CompletedAt < now.AddDays(-1) &&
                        !((x.Status == "Indeterminate" && x.Operation != "MdbStatus" ||
                           x.Status == "Failed" && x.Operation != "MdbStatus" &&
                           (x.Error == "ack_timeout" || x.Error == "command_outcome_unknown")) &&
                          !db.TelemetryCommandResolutions.Any(resolution =>
                              resolution.TelemetryCommandDispatchId == x.Id)))
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
                        new DeviceCommandResult(false, 0, 0,
                            dispatch.Operation == "Disconnect" ? "command_outcome_unknown" : "owner_changed_before_send"),
                        "Failed", cancellationToken);
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
                        new DeviceCommandResult(false, 0, 0,
                            dispatch.Operation == "Disconnect" ? "command_outcome_unknown" : "owner_socket_unavailable"),
                        "Failed", cancellationToken);
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
            else if (dispatch.Operation == "Disconnect")
            {
                using var payloadDocument = JsonDocument.Parse(dispatch.PayloadJson);
                var expectedOwnerConnectionId = payloadDocument.RootElement.TryGetProperty("ownerConnectionId", out var ownerConnectionElement) &&
                                                ownerConnectionElement.ValueKind == JsonValueKind.String
                    ? ownerConnectionElement.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(expectedOwnerConnectionId) ||
                    !string.Equals(connection.ConnectionId, expectedOwnerConnectionId, StringComparison.Ordinal))
                {
                    await CompleteCommandDispatchAsync(dispatchId,
                        new DeviceCommandResult(false, 0, 0, "owner_changed_before_send"), "Failed", cancellationToken);
                    return;
                }

                var reason = payloadDocument.RootElement.TryGetProperty("reason", out var reasonElement) &&
                             reasonElement.ValueKind == JsonValueKind.String
                    ? reasonElement.GetString()
                    : null;
                var disconnected = await DisconnectConnectionAsync(
                    NormalizeSerial(serial), connection,
                    string.IsNullOrWhiteSpace(reason) ? "Credencial de telemetria alterada." : reason);
                if (!disconnected)
                {
                    await CompleteCommandDispatchAsync(dispatchId,
                        new DeviceCommandResult(false, 0, 0, "owner_changed_before_send"), "Failed", cancellationToken);
                    return;
                }

                await CompleteCommandDispatchAsync(dispatchId,
                    new DeviceCommandResult(true, 0, 0), "Completed", cancellationToken);
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
            var limit = DateTime.UtcNow - MachineConnectionFreshness.Window;
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
        var id = 0;
        var hasMessageId = root.TryGetProperty("id", out var idElement) && idElement.TryGetInt32(out id);

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

        if (type != "msg") return;
        if (!hasMessageId)
            throw new WebSocketException("Mensagem MDB sem identificador inteiro; o frame não será confirmado.");
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new WebSocketException("Mensagem MDB sem objeto data; o frame não será confirmado.");

        if (!await IsCurrentLocalOwnerAsync(connection, cancellationToken))
        {
            _logger.LogWarning(
                "Mensagem MDB rejeitada de uma conexão que perdeu a propriedade para {Serial}; o frame não será persistido nem confirmado.",
                connection.Serial);
            throw new WebSocketException("A conexão MDB não é mais a proprietária da máquina.");
        }

        var isStatusResponse = data.ValueKind == JsonValueKind.Object &&
                               data.TryGetProperty("command", out var commandElement) &&
                               commandElement.ValueKind == JsonValueKind.String &&
                               string.Equals(commandElement.GetString(), "status", StringComparison.OrdinalIgnoreCase);
        PendingStatusRequest? pendingStatusRequest = null;
        InboundStatusResponse? pendingStatusResponse = null;
        if (isStatusResponse)
        {
            var status = data.TryGetProperty("status", out var statusElement)
                ? statusElement.GetString()
                : null;
            var pendingStatus = Volatile.Read(ref connection.PendingStatusRequest);
            if (pendingStatus?.MessageId == id)
            {
                pendingStatusRequest = pendingStatus;
                pendingStatusResponse = new InboundStatusResponse(
                    status,
                    data.GetRawText(),
                    DateTime.UtcNow);
            }
        }

        // Deduplicação local precisa usar o mesmo conteúdo que a chave durável. O firmware
        // pode reutilizar um ID com outro payload sem derrubar o socket; só conteúdo igual
        // é retransmissão do mesmo frame.
        var payloadFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data.GetRawText())));
        var receivedMessages = isStatusResponse ? connection.ReceivedStatusIds : connection.ReceivedIds;
        if (receivedMessages.TryGetValue(id, out var previousFingerprint) &&
            string.Equals(previousFingerprint, payloadFingerprint, StringComparison.Ordinal))
        {
            await QueuePriorityAsync(connection, new { id, target = connection.Serial, type = "ack", data = "OK" }, cancellationToken);
            return;
        }
        receivedMessages[id] = payloadFingerprint;
        if (receivedMessages.Count > 512)
        {
            foreach (var oldId in receivedMessages.Keys.OrderBy(x => x).Take(128))
                receivedMessages.TryRemove(oldId, out _);
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
            receivedAt,
            NodeId);
        using (var scope = _scopeFactory.CreateScope())
        {
            var inbox = scope.ServiceProvider.GetRequiredService<DeviceMessageInboxService>();
            if (!await inbox.PersistBeforeAcknowledgementAsync(inbound, cancellationToken))
                throw new WebSocketException("A propriedade MDB mudou antes da persistência da mensagem.");
        }
        await QueuePriorityAsync(connection, new { id, target = connection.Serial, type = "ack", data = "OK" }, cancellationToken);
        if (pendingStatusRequest is not null && pendingStatusResponse is not null)
            pendingStatusRequest.Completion.TrySetResult(pendingStatusResponse);

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

    private async Task MarkOnlineAsync(
        AppDbContext db,
        DeviceConnection connection,
        CancellationToken cancellationToken)
    {
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
        state.MdbMenuPhase = null;
        state.MdbMenuConnectionId = null;
        state.MdbMenuSessionId = null;
        state.MdbMenuUpdatedAt = null;
        state.IsOnline = true;
        state.ConnectionId = connection.ConnectionId;
        state.OwnerNodeId = NodeId;
        state.OwnerConnectionId = connection.ConnectionId;
        state.CommandRoutingVersion = CurrentCommandRoutingVersion;
        state.OwnerLeaseExpiresAt = DateTime.UtcNow.Add(TelemetryConnectionLeaseWorker.LeaseDuration);
        state.ConnectedAt = connection.ConnectedAt;
        state.LastSeenAt = connection.ConnectedAt;
        state.MdbStatusRequestAt = null;
        state.MdbStatusRequestMessageId = null;
        db.TelemetryEvents.Add(new TelemetryEvent
        {
            CompanyId = connection.CompanyId,
            MachineId = connection.MachineId,
            EventType = "device.connected"
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task BroadcastDeviceOnlineAsync(DeviceConnection connection)
    {
        try
        {
            await _panelHub.BroadcastCompanyAsync(connection.CompanyId, new
            {
                type = "connection.updated",
                machineId = connection.MachineId,
                online = true,
                commandRouteAvailable = true,
                connectedAt = connection.ConnectedAt,
                lastSeenAt = connection.ConnectedAt
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Não foi possível atualizar o painel após a conexão MDB da máquina {MachineId}; o socket permanece ativo.",
                connection.MachineId);
        }
    }

    private async Task<bool> IsCurrentLocalOwnerAsync(
        DeviceConnection connection,
        CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.MachineConnectionStates.AsNoTracking().AnyAsync(
                state => state.MachineId == connection.MachineId &&
                         state.CompanyId == connection.CompanyId &&
                         state.IsOnline &&
                         state.ConnectionId == connection.ConnectionId &&
                         state.OwnerNodeId == NodeId &&
                         state.OwnerConnectionId == connection.ConnectionId &&
                         state.CommandRoutingVersion == CurrentCommandRoutingVersion,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Não foi possível confirmar a propriedade do socket MDB para {Serial}; nenhum comando será enviado.",
                connection.Serial);
            return false;
        }
    }

    private async Task<bool> MarkOfflineAsync(DeviceConnection connection, CancellationToken cancellationToken = default)
    {
        var disconnectedAt = DateTime.UtcNow;
        var disconnectedCurrentOwner = false;
        try
        {
            for (var attempt = 1; attempt <= OfflinePersistenceMaxAttempts; attempt++)
            {
                try
                {
                    disconnectedCurrentOwner = await PersistOfflineStateAsync(
                        connection,
                        disconnectedAt,
                        cancellationToken);
                    break;
                }
                catch (Exception exception) when (
                    attempt < OfflinePersistenceMaxAttempts && IsTransientOfflinePersistenceError(exception))
                {
                    var exponentialDelay = 25 * (1 << (attempt - 1));
                    var retryDelay = TimeSpan.FromMilliseconds(exponentialDelay + Random.Shared.Next(0, 26));
                    _logger.LogWarning(
                        exception,
                        "Falha transitória ao persistir desconexão de {MachineId}; nova tentativa {Attempt}/{MaxAttempts} em {RetryDelayMs} ms.",
                        connection.MachineId,
                        attempt + 1,
                        OfflinePersistenceMaxAttempts,
                        retryDelay.TotalMilliseconds);
                    await Task.Delay(retryDelay, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Prazo de persistência da desconexão de {Serial} atingido durante o encerramento.", connection.Serial);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao persistir desconexão de {Serial}.", connection.Serial);
            return false;
        }

        if (!disconnectedCurrentOwner) return true;

        try
        {
            await _panelHub.BroadcastCompanyAsync(connection.CompanyId, new
            {
                type = "connection.updated", machineId = connection.MachineId, online = false,
                commandRouteAvailable = false,
                disconnectedAt
            }).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Prazo de publicação da desconexão de {Serial} atingido durante o encerramento.", connection.Serial);
        }
        catch (Exception ex) { _logger.LogError(ex, "Falha ao publicar desconexão de {Serial}.", connection.Serial); }
        return true;
    }

    private async Task<bool> PersistOfflineStateAsync(
        DeviceConnection connection,
        DateTime disconnectedAt,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var disconnectedCurrentOwner = await db.MachineConnectionStates
            .Where(x => x.MachineId == connection.MachineId &&
                        x.CompanyId == connection.CompanyId &&
                        x.ConnectionId == connection.ConnectionId &&
                        x.OwnerNodeId == NodeId &&
                        x.OwnerConnectionId == connection.ConnectionId &&
                        x.CommandRoutingVersion == CurrentCommandRoutingVersion)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.IsOnline, false)
                .SetProperty(x => x.ConnectionId, (string?)null)
                .SetProperty(x => x.OwnerNodeId, (string?)null)
                .SetProperty(x => x.OwnerConnectionId, (string?)null)
                .SetProperty(x => x.CommandRoutingVersion, (int?)null)
                .SetProperty(x => x.OwnerLeaseExpiresAt, (DateTime?)null)
                .SetProperty(x => x.DisconnectedAt, disconnectedAt), cancellationToken) > 0;

        if (!disconnectedCurrentOwner)
        {
            await transaction.RollbackAsync(cancellationToken);
            _logger.LogDebug(
                "A desconexão de {MachineId} não alterou presença porque a conexão encerrada não é mais a proprietária persistida.",
                connection.MachineId);
            return false;
        }

        db.TelemetryEvents.Add(new TelemetryEvent
        {
            CompanyId = connection.CompanyId,
            MachineId = connection.MachineId,
            EventType = "device.disconnected"
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static bool IsTransientOfflinePersistenceError(Exception exception)
    {
        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            if (current is SqliteException sqlite && sqlite.SqliteErrorCode is 5 or 6)
                return true;

            if (current is PostgresException postgres && postgres.SqlState is "40001" or "40P01" or "55P03")
                return true;

            if (current is NpgsqlException npgsql && npgsql.IsTransient)
                return true;
        }

        return false;
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
                        cancellationToken);
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

    private static async Task CloseRejectedSocketAsync(
        WebSocket socket,
        WebSocketCloseStatus closeStatus,
        string reason,
        CancellationToken cancellationToken)
    {
        if (socket.State != WebSocketState.Open) return;

        using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(WebSocketCloseTimeoutSeconds));
        using var linkedTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, closeTimeout.Token);
        try
        {
            await socket.CloseAsync(closeStatus, reason, linkedTimeout.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or InvalidOperationException or OperationCanceledException)
        {
            // Rejected connections are aborted by HandleDeviceAsync after this best-effort close.
        }
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
                if (!_requireDeviceCredentials && !connection.IsAuthenticated && previous.Socket.State == WebSocketState.Open)
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
        public DeviceConnection(string machineId, Guid companyId, string serial, WebSocket socket, bool isAuthenticated)
        {
            MachineId = machineId;
            CompanyId = companyId;
            Serial = serial;
            Socket = socket;
            IsAuthenticated = isAuthenticated;
        }

        public string MachineId { get; }
        public Guid CompanyId { get; }
        public string Serial { get; }
        public string ConnectionId { get; } = Guid.NewGuid().ToString("N");
        public WebSocket Socket { get; }
        public bool IsAuthenticated { get; }
        public DateTime ConnectedAt { get; set; } = DateTime.UtcNow;
        public CancellationTokenSource Cts { get; } = new();
        public CancellationTokenSource WriterCts { get; } = new();
        public Task? WriterTask { get; set; }
        public TaskCompletionSource<bool> Disconnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DrainSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CloseSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public SemaphoreSlim CommandGate { get; } = new(1, 1);
        public SemaphoreSlim InboundGate { get; } = new(1, 1);
        public SemaphoreSlim OutboundGate { get; } = new(1, 1);
        public ConcurrentDictionary<int, TaskCompletionSource<bool>> PendingAcks { get; } = new();
        public PendingStatusRequest? PendingStatusRequest;
        public ConcurrentDictionary<int, string> ReceivedIds { get; } = new();
        public ConcurrentDictionary<int, string> ReceivedStatusIds { get; } = new();
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
