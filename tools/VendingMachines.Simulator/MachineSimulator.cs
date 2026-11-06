using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace VendingMachines.Simulator;

public sealed class MachineSimulator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly CliOptions _options;
    private readonly SimulatorLog _log;
    private readonly object _connectionLock = new();
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly SemaphoreSlim _scenarioGate = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<bool>> _pendingAcks = new();
    private readonly ConcurrentDictionary<int, byte> _receivedMessageIds = new();
    private ClientWebSocket? _socket;
    private Channel<string>? _outbound;
    private CancellationToken _lifetimeToken;
    private int _lastMessageId;
    private int _signal;
    private int _sentMessages;
    private int _receivedMessages;
    private volatile SimulatorState _state = SimulatorState.Disconnected;
    private ScenarioPlan _scenario = ScenarioPlan.None;
    private string? _transactionId;
    private int _selectedPrice;
    private DateTime? _connectedAt;
    private DateTime? _lastPongAt;
    private string? _mdbStatusOverride;

    public MachineSimulator(CliOptions options, SimulatorLog log)
    {
        _options = options;
        _log = log;
        _signal = options.Signal;
    }

    public string Serial => _options.Serial;
    public SimulatorState State => _state;
    public bool IsConnected => _socket?.State == WebSocketState.Open;
    public ScenarioPlan Scenario => _scenario;
    public string? TransactionId => _transactionId;
    public int Signal => Volatile.Read(ref _signal);
    public int SentMessages => Volatile.Read(ref _sentMessages);
    public int ReceivedMessages => Volatile.Read(ref _receivedMessages);
    public DateTime? ConnectedAt => _connectedAt;
    public DateTime? LastPongAt => _lastPongAt;
    public string MdbStatus => _mdbStatusOverride ?? GetAutomaticMdbStatus();

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _lifetimeToken = cancellationToken;
        var reconnectAttempt = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunConnectionAsync(cancellationToken);
                reconnectAttempt = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _log.Error(Serial, $"Conexão encerrada: {ex.Message}");
            }
            finally
            {
                SetDisconnected();
            }

            if (!_options.Reconnect || cancellationToken.IsCancellationRequested) break;

            reconnectAttempt++;
            var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(5, reconnectAttempt - 1))));
            _log.Warning(Serial, $"Reconectando em {delay.TotalSeconds:0} segundo(s). Tentativa {reconnectAttempt}.");
            try { await Task.Delay(delay, cancellationToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public void ArmScenario(ScenarioPlan scenario)
    {
        _scenario = scenario;
        _log.Info(Serial, $"Cenário preparado: {DescribeScenario(scenario)}.");

        if (_state == SimulatorState.AwaitingSelection)
            RunDetached(ct => ContinueScenarioAfterSessionStartAsync(scenario, ct));
    }

    public async Task SelectProductAsync(int itemNumber, int itemPrice, CancellationToken cancellationToken = default)
    {
        if (_state != SimulatorState.AwaitingSelection)
            throw new InvalidOperationException("A máquina não está aguardando a seleção de um produto.");
        if (itemNumber < 0 || itemPrice <= 0)
            throw new ArgumentOutOfRangeException(nameof(itemNumber), "Item e preço precisam ser válidos.");

        _selectedPrice = itemPrice;
        var sent = await SendMessageAsync(new
        {
            command = "vend",
            type = "request",
            itemPrice,
            itemNumber
        }, cancellationToken);
        if (sent) _state = SimulatorState.WaitingPix;
    }

    public Task DeliverSuccessAsync(CancellationToken cancellationToken = default) =>
        CompleteDeliveryAsync(true, null, cancellationToken);

    public Task DeliverFailureAsync(string reason, CancellationToken cancellationToken = default) =>
        CompleteDeliveryAsync(false, string.IsNullOrWhiteSpace(reason) ? "unknown" : reason.Trim(), cancellationToken);

    public async Task CancelFromMachineAsync(CancellationToken cancellationToken = default)
    {
        if (_state is SimulatorState.Disconnected or SimulatorState.Connecting or SimulatorState.Connected)
            throw new InvalidOperationException("Não existe uma sessão em andamento.");
        await CloseSessionAsync(cancellationToken);
    }

    public async Task SendCustomAsync(JsonElement data, CancellationToken cancellationToken = default)
    {
        if (data.ValueKind is not (JsonValueKind.Object or JsonValueKind.String))
            throw new ArgumentException("O payload deve ser um objeto JSON ou uma string JSON.");
        await SendMessageAsync(data.Clone(), cancellationToken);
    }

    public void SetSignal(int signal)
    {
        if (signal is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(signal), "O sinal deve estar entre 0 e 100.");
        Volatile.Write(ref _signal, signal);
        _log.Info(Serial, $"Sinal alterado para {signal}.");
    }

    public void SetMdbStatus(string? status)
    {
        if (status == null || status.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            _mdbStatusOverride = null;
            _log.Info(Serial, $"Status MDB em modo automático: {MdbStatus}.");
            return;
        }

        var normalized = status.Trim().ToLowerInvariant();
        if (normalized is not ("inactive_state" or "disable_state" or "enabled_state" or "idle_state" or "vend_state"))
        {
            throw new InvalidOperationException(
                "Status MDB inválido. Use inactive_state, disable_state, enabled_state, idle_state, vend_state ou auto.");
        }

        _mdbStatusOverride = normalized;
        _log.Info(Serial, $"Status MDB fixado em {normalized}.");
    }

    public void Reconnect()
    {
        _log.Warning(Serial, "Reconexão manual solicitada.");
        lock (_connectionLock) _socket?.Abort();
    }

    private async Task RunConnectionAsync(CancellationToken lifetimeToken)
    {
        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        var outbound = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

        _state = SimulatorState.Connecting;
        _log.Info(Serial, $"Conectando em {_options.ServerUri}...");
        await socket.ConnectAsync(_options.ServerUri, lifetimeToken);

        lock (_connectionLock)
        {
            _socket = socket;
            _outbound = outbound;
        }

        var writer = WriteLoopAsync(socket, outbound.Reader, connectionCts.Token);
        var receiver = ReceiveLoopAsync(socket, connectionCts.Token);
        var heartbeat = HeartbeatLoopAsync(connectionCts.Token);

        try
        {
            var startId = NextMessageId();
            var startAck = RegisterAck(startId);
            await QueueAsync(JsonSerializer.Serialize(new
            {
                id = startId,
                type = "start",
                target = Serial
            }, JsonOptions), connectionCts.Token);
            _log.Sent(Serial, $"start id={startId} target={Serial}");

            await startAck.Task.WaitAsync(TimeSpan.FromSeconds(10), connectionCts.Token);
            _state = SimulatorState.Connected;
            _connectedAt = DateTime.UtcNow;
            _log.Info(Serial, "Máquina conectada e identificada.");

            await Task.WhenAny(writer, receiver, heartbeat);
            if (receiver.IsFaulted) await receiver;
            if (writer.IsFaulted) await writer;
            if (heartbeat.IsFaulted) await heartbeat;
        }
        finally
        {
            connectionCts.Cancel();
            outbound.Writer.TryComplete();
            CancelPendingAcks();
            try { await Task.WhenAll(IgnoreCancellation(writer), IgnoreCancellation(receiver), IgnoreCancellation(heartbeat)); } catch { }
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Simulador encerrado", CancellationToken.None); } catch { }
            }
            lock (_connectionLock)
            {
                if (ReferenceEquals(_socket, socket))
                {
                    _socket = null;
                    _outbound = null;
                }
            }
        }
    }

    private async Task WriteLoopAsync(ClientWebSocket socket, ChannelReader<string> reader, CancellationToken cancellationToken)
    {
        await foreach (var json in reader.ReadAllAsync(cancellationToken))
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
            Interlocked.Increment(ref _sentMessages);
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var text = await ReadTextAsync(socket, cancellationToken);
            if (text == null) return;
            Interlocked.Increment(ref _receivedMessages);
            await HandleIncomingAsync(text, cancellationToken);
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.HeartbeatInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            if (!IsConnected) continue;
            var id = NextMessageId();
            await QueueEnvelopeAsync(new
            {
                id,
                type = "ping",
                target = Serial,
                clientId = "",
                machineId = "",
                signal = Signal
            }, cancellationToken);
            _log.Sent(Serial, $"ping id={id} signal={Signal}");
        }
    }

    private async Task HandleIncomingAsync(string text, CancellationToken cancellationToken)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(text); }
        catch (JsonException)
        {
            _log.Warning(Serial, $"JSON inválido recebido: {text}");
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            var type = GetString(root, "type");
            var id = GetInt(root, "id", 0);

            if (type == "ack")
            {
                _log.Received(Serial, $"ack id={id}");
                if (_pendingAcks.TryRemove(id, out var pending)) pending.TrySetResult(true);
                return;
            }

            if (type == "pong")
            {
                _lastPongAt = DateTime.UtcNow;
                _log.Received(Serial, $"pong id={id} status={GetString(root, "status") ?? "ok"}");
                return;
            }

            if (type == "ping")
            {
                await QueueEnvelopeAsync(new { id, type = "pong", status = "ok" }, cancellationToken);
                _log.Sent(Serial, $"pong id={id}");
                return;
            }

            if (type != "msg" || !root.TryGetProperty("data", out var data))
            {
                _log.Received(Serial, text);
                return;
            }

            if (!_receivedMessageIds.TryAdd(id, 0)) return;
            TrimReceivedIds();

            if (data.ValueKind == JsonValueKind.String)
            {
                var command = data.GetString() ?? string.Empty;
                _log.Received(Serial, $"msg id={id} data={command}");
                if (command.Equals("MDB_STATUS", StringComparison.OrdinalIgnoreCase))
                {
                    RunDetached(ct => SendMdbStatusAsync(id, ct), cancellationToken);
                    return;
                }

                await QueueEnvelopeAsync(new { id, target = Serial, type = "ack", data = "OK" }, cancellationToken);
                _log.Sent(Serial, $"ack id={id}");
                HandleServerCommand(command, cancellationToken);
                return;
            }

            if (data.ValueKind == JsonValueKind.Object)
            {
                await QueueEnvelopeAsync(new { id, target = Serial, type = "ack", data = "OK" }, cancellationToken);
                _log.Sent(Serial, $"ack id={id}");
                var clone = data.Clone();
                _log.Received(Serial, $"msg id={id} data={clone.GetRawText()}");
                HandleServerPayload(clone, cancellationToken);
            }
        }
    }

    private async Task SendMdbStatusAsync(int requestId, CancellationToken cancellationToken)
    {
        var status = MdbStatus;
        await QueueEnvelopeAsync(new
        {
            id = requestId,
            target = Serial,
            type = "msg",
            data = new
            {
                command = "status",
                status
            }
        }, cancellationToken);
        _log.Sent(Serial, $"status id={requestId} status={status} (sem ACK separado)");
    }

    private void HandleServerCommand(string command, CancellationToken connectionToken)
    {
        switch (command.ToUpperInvariant())
        {
            case "ABRIR_SESSAO":
                _transactionId = null;
                _selectedPrice = 0;
                RunDetached(HandleOpenSessionAsync, connectionToken);
                break;
            case "FECHAR_SESSAO":
                RunDetached(HandleServerCloseAsync, connectionToken);
                break;
            case "VENDA_APROVADA":
                RunDetached(HandleApprovedAsync, connectionToken);
                break;
            case "VENDA_NEGADA":
                RunDetached(HandleDeniedAsync, connectionToken);
                break;
            default:
                _log.Warning(Serial, $"Comando do servidor não reconhecido: {command}");
                break;
        }
    }

    private void HandleServerPayload(JsonElement data, CancellationToken connectionToken)
    {
        var command = GetString(data, "command");
        var type = GetString(data, "type");
        if (command == "pay" && type == "pix")
        {
            _transactionId = GetString(data, "transactionId");
            _state = SimulatorState.WaitingPaymentResult;
            var code = GetString(data, "code") ?? "(não informado)";
            var amount = GetInt(data, "amount", 0);
            var expiresAt = GetString(data, "expiresAt") ?? "(não informado)";
            _log.Info(Serial, $"Pix recebido: valor={amount} transação={_transactionId ?? "sem-id"} expira={expiresAt}");
            Console.WriteLine($"PIX COPIA E COLA: {code}");

            if (_scenario.Kind == ScenarioKind.CancelAfterPix)
                RunDetached(async ct =>
                {
                    await Task.Delay(_scenario.Delay ?? TimeSpan.FromSeconds(1), ct);
                    await CloseSessionAsync(ct);
                }, connectionToken);
        }
    }

    private async Task HandleOpenSessionAsync(CancellationToken cancellationToken)
    {
        await _scenarioGate.WaitAsync(cancellationToken);
        try
        {
            var started = await SendMessageAsync(new
            {
                command = "pool",
                type = "begin_session",
                fundsAvailable = 1000
            }, cancellationToken);
            if (!started) return;

            _state = SimulatorState.AwaitingSelection;
            await ContinueScenarioAfterSessionStartAsync(_scenario, cancellationToken);
        }
        finally { _scenarioGate.Release(); }
    }

    private async Task ContinueScenarioAfterSessionStartAsync(ScenarioPlan scenario, CancellationToken cancellationToken)
    {
        if (scenario.Kind == ScenarioKind.None) return;
        await Task.Delay(scenario.Delay ?? TimeSpan.FromMilliseconds(300), cancellationToken);

        if (scenario.Kind == ScenarioKind.CancelBeforeSelection)
        {
            await CloseSessionAsync(cancellationToken);
            return;
        }

        if (scenario.Kind is ScenarioKind.SaleSuccess or ScenarioKind.Sale or ScenarioKind.CancelAfterPix or ScenarioKind.DeliveryFailure)
            await SelectProductAsync(scenario.ItemNumber, scenario.ItemPrice, cancellationToken);
    }

    private async Task HandleApprovedAsync(CancellationToken cancellationToken)
    {
        _state = SimulatorState.PaymentApproved;
        var approved = await SendMessageAsync(WithTransaction(new Dictionary<string, object?>
        {
            ["command"] = "pool",
            ["type"] = "vend_approved",
            ["vendAmount"] = _selectedPrice
        }), cancellationToken);
        if (!approved) return;

        await Task.Delay(_scenario.Delay ?? TimeSpan.FromMilliseconds(500), cancellationToken);
        if (_scenario.Kind == ScenarioKind.SaleSuccess)
            await CompleteDeliveryAsync(true, null, cancellationToken);
        else if (_scenario.Kind == ScenarioKind.DeliveryFailure)
            await CompleteDeliveryAsync(false, _scenario.FailureReason, cancellationToken);
        else
            _log.Info(Serial, "Pagamento aprovado. Use 'deliver success' ou 'deliver failure <motivo>'.");
    }

    private async Task HandleDeniedAsync(CancellationToken cancellationToken)
    {
        await SendMessageAsync(WithTransaction(new Dictionary<string, object?>
        {
            ["command"] = "pool",
            ["type"] = "vend_denied"
        }), cancellationToken);
        await CloseSessionAsync(cancellationToken);
    }

    private async Task HandleServerCloseAsync(CancellationToken cancellationToken)
    {
        _state = SimulatorState.Closing;
        await SendMessageAsync(WithTransaction(new Dictionary<string, object?>
        {
            ["command"] = "pool",
            ["type"] = "end_session"
        }), cancellationToken);
        FinishSession();
    }

    private async Task CompleteDeliveryAsync(bool success, string? reason, CancellationToken cancellationToken)
    {
        if (_state != SimulatorState.PaymentApproved)
            throw new InvalidOperationException("A venda ainda não foi aprovada para entrega.");

        var payload = new Dictionary<string, object?>
        {
            ["command"] = "vend",
            ["type"] = success ? "success" : "failure"
        };
        if (!success) payload["reason"] = reason ?? "unknown";
        await SendMessageAsync(WithTransaction(payload), cancellationToken);
        await CloseSessionAsync(cancellationToken);
    }

    private async Task CloseSessionAsync(CancellationToken cancellationToken)
    {
        _state = SimulatorState.Closing;
        await SendMessageAsync(WithTransaction(new Dictionary<string, object?>
        {
            ["command"] = "vend",
            ["type"] = "sessioncomplete"
        }), cancellationToken);
        await SendMessageAsync(WithTransaction(new Dictionary<string, object?>
        {
            ["command"] = "pool",
            ["type"] = "end_session"
        }), cancellationToken);
        FinishSession();
    }

    private void FinishSession()
    {
        _state = SimulatorState.Completed;
        _log.Info(Serial, "Sessão encerrada pela máquina.");
        _scenario = ScenarioPlan.None;
        _transactionId = null;
        _selectedPrice = 0;
    }

    private string GetAutomaticMdbStatus() => _state switch
    {
        SimulatorState.Disconnected or SimulatorState.Connecting => "inactive_state",
        SimulatorState.AwaitingSelection => "idle_state",
        SimulatorState.WaitingPix or
            SimulatorState.WaitingPaymentResult or
            SimulatorState.PaymentApproved or
            SimulatorState.Closing => "vend_state",
        SimulatorState.Failed => "disable_state",
        _ => "enabled_state"
    };

    private Dictionary<string, object?> WithTransaction(Dictionary<string, object?> payload)
    {
        if (!string.IsNullOrWhiteSpace(_transactionId)) payload["transactionId"] = _transactionId;
        return payload;
    }

    private async Task<bool> SendMessageAsync(object data, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken, cancellationToken);
        await _commandGate.WaitAsync(linked.Token);
        var id = NextMessageId();
        try
        {
            var json = JsonSerializer.Serialize(new { id, target = Serial, type = "msg", data }, JsonOptions);
            var ack = RegisterAck(id);
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                await QueueAsync(json, linked.Token);
                _log.Sent(Serial, $"msg id={id} tentativa={attempt} data={JsonSerializer.Serialize(data, JsonOptions)}");
                try
                {
                    await ack.Task.WaitAsync(TimeSpan.FromSeconds(5), linked.Token);
                    return true;
                }
                catch (TimeoutException) when (attempt < 3)
                {
                    _log.Warning(Serial, $"ACK não recebido para id={id}; repetindo.");
                }
            }
            _log.Error(Serial, $"ACK não recebido para id={id} após 3 tentativas.");
            return false;
        }
        finally
        {
            _pendingAcks.TryRemove(id, out _);
            _commandGate.Release();
        }
    }

    private Task QueueEnvelopeAsync(object payload, CancellationToken cancellationToken) =>
        QueueAsync(JsonSerializer.Serialize(payload, JsonOptions), cancellationToken);

    private async Task QueueAsync(string json, CancellationToken cancellationToken)
    {
        Channel<string>? outbound;
        lock (_connectionLock) outbound = _outbound;
        if (outbound == null || !IsConnected) throw new WebSocketException("A máquina está offline.");
        await outbound.Writer.WriteAsync(json, cancellationToken);
    }

    private TaskCompletionSource<bool> RegisterAck(int id)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingAcks[id] = completion;
        return completion;
    }

    private void CancelPendingAcks()
    {
        foreach (var pending in _pendingAcks.Values) pending.TrySetCanceled();
        _pendingAcks.Clear();
    }

    private void SetDisconnected()
    {
        _state = SimulatorState.Disconnected;
        _connectedAt = null;
        _transactionId = null;
        _receivedMessageIds.Clear();
        _log.Warning(Serial, "Máquina offline.");
    }

    private void RunDetached(Func<CancellationToken, Task> action, CancellationToken? connectionToken = null)
    {
        var token = connectionToken ?? _lifetimeToken;
        _ = Task.Run(async () =>
        {
            try { await action(token); }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _state = SimulatorState.Failed;
                _log.Error(Serial, ex.Message);
            }
        }, token);
    }

    private void TrimReceivedIds()
    {
        if (_receivedMessageIds.Count <= 512) return;
        foreach (var id in _receivedMessageIds.Keys.Order().Take(128)) _receivedMessageIds.TryRemove(id, out _);
    }

    private int NextMessageId() => Interlocked.Increment(ref _lastMessageId);

    private static async Task<string?> ReadTextAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text) continue;
            stream.Write(buffer, 0, result.Count);
            if (stream.Length > 64 * 1024) throw new WebSocketException("Mensagem maior que 64 KB.");
            if (result.EndOfMessage) return Encoding.UTF8.GetString(stream.ToArray());
        }
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int GetInt(JsonElement element, string property, int fallback) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var parsed) ? parsed : fallback;

    private static async Task IgnoreCancellation(Task task)
    {
        try { await task; } catch (OperationCanceledException) { }
    }

    private static string DescribeScenario(ScenarioPlan scenario) => scenario.Kind switch
    {
        ScenarioKind.SaleSuccess => $"venda aprovada com entrega (item {scenario.ItemNumber}, {scenario.ItemPrice} centavos)",
        ScenarioKind.Sale => $"venda com entrega manual (item {scenario.ItemNumber}, {scenario.ItemPrice} centavos)",
        ScenarioKind.CancelBeforeSelection => "cancelamento antes da seleção",
        ScenarioKind.CancelAfterPix => $"cancelamento depois do Pix (item {scenario.ItemNumber}, {scenario.ItemPrice} centavos)",
        ScenarioKind.DeliveryFailure => $"falha na entrega: {scenario.FailureReason}",
        _ => "nenhum"
    };
}
