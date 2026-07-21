using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;

namespace VendingMachines.Api.Services;

public sealed class SessionOrchestrator
{
    private static readonly string[] TerminalStates =
    {
        MachineSessionStates.Completed,
        MachineSessionStates.ClosedWithoutSelection,
        MachineSessionStates.ClosedByDeviceBeforeSelection,
        MachineSessionStates.ClosureIncomplete,
        MachineSessionStates.Denied,
        MachineSessionStates.Refunded,
        MachineSessionStates.ReconciliationRequired
    };

    private readonly AppDbContext _db;
    private readonly TelemetryConnectionManager _connections;
    private readonly TelemetryPanelHub _panelHub;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<SessionOrchestrator> _logger;

    public SessionOrchestrator(
        AppDbContext db,
        TelemetryConnectionManager connections,
        TelemetryPanelHub panelHub,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        ILogger<SessionOrchestrator> logger)
    {
        _db = db;
        _connections = connections;
        _panelHub = panelHub;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _environment = environment;
        _logger = logger;
    }

    public async Task<MachineSession> StartSessionAsync(
        string machineId,
        Guid companyId,
        Guid? userId,
        string source,
        CancellationToken cancellationToken)
    {
        var machine = await _db.Machines.FirstOrDefaultAsync(
            x => x.Id == machineId && x.CompanyId == companyId, cancellationToken)
            ?? throw new KeyNotFoundException("Máquina não encontrada.");

        if (!_connections.IsOnline(machine.SerialNumber))
            throw new InvalidOperationException("machine_offline");

        var existing = await ActiveSessions().FirstOrDefaultAsync(x => x.MachineId == machineId, cancellationToken);
        if (existing != null) throw new InvalidOperationException("session_already_active");

        var session = new MachineSession
        {
            MachineId = machineId,
            CompanyId = companyId,
            StartedByUserId = userId,
            Source = source,
            State = MachineSessionStates.Opening
        };
        _db.MachineSessions.Add(session);
        await AddEventAsync(session, "session.opening", "Sessão MDB solicitada.", userId, null, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        var result = await SendTrackedCommandAsync(machine, session, "ABRIR_SESSAO", userId, cancellationToken);
        if (!result.Success)
        {
            session.State = MachineSessionStates.ClosureIncomplete;
            session.CloseReason = result.Error;
            session.ClosedAt = DateTime.UtcNow;
            await AddEventAsync(session, "command.failed", "Não foi possível abrir a sessão.", userId, null, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            throw new InvalidOperationException(result.Error ?? "command_failed");
        }

        await BroadcastSessionAsync(session);
        return session;
    }

    public async Task<MachineSession> CancelSessionAsync(
        Guid sessionId,
        Guid companyId,
        Guid? userId,
        string reason,
        CancellationToken cancellationToken)
    {
        var session = await _db.MachineSessions.Include(x => x.Machine).Include(x => x.Transaction)
            .FirstOrDefaultAsync(x => x.Id == sessionId && x.CompanyId == companyId, cancellationToken)
            ?? throw new KeyNotFoundException("Sessão não encontrada.");

        if (session.ClosedAt.HasValue) return session;
        var machine = session.Machine ?? throw new InvalidOperationException("Máquina da sessão não encontrada.");

        if (session.State is MachineSessionStates.Opening or MachineSessionStates.AwaitingSelection)
        {
            session.State = MachineSessionStates.ClosingWithoutSelection;
            session.CloseReason = reason;
            session.SelectionDeadlineAt = null;
            session.LastEventAt = DateTime.UtcNow;
            await AddEventAsync(session, "session.closing_without_selection", reason, userId, null, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            await SendTrackedCommandAsync(machine, session, "FECHAR_SESSAO", userId, cancellationToken);
        }
        else if (session.State == MachineSessionStates.PaymentPending)
        {
            await CancelPendingPaymentAsync(session, reason, userId, cancellationToken);
        }
        else
        {
            throw new InvalidOperationException("payment_already_approved");
        }

        await BroadcastSessionAsync(session);
        return session;
    }

    public async Task HandleDeviceMessageAsync(InboundDeviceMessage message, CancellationToken cancellationToken)
    {
        JsonElement data;
        try { data = JsonSerializer.Deserialize<JsonElement>(message.DataJson); }
        catch (JsonException) { return; }
        if (data.ValueKind != JsonValueKind.Object) return;

        var command = GetString(data, "command");
        var type = GetString(data, "type");
        if (string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(type)) return;

        var session = await ActiveSessions().Include(x => x.Machine).Include(x => x.Transaction)
            .FirstOrDefaultAsync(x => x.MachineId == message.MachineId, cancellationToken);
        if (session == null)
        {
            await AddStandaloneEventAsync(message, $"{command}.{type}", "Evento recebido sem sessão ativa.", message.DataJson, cancellationToken);
            return;
        }

        session.LastEventAt = message.ReceivedAt;
        await AddEventAsync(session, $"{command}.{type}", null, null, message.DataJson, cancellationToken);

        var transactionCorrelation = GetTransactionCorrelation(session, data);
        if (transactionCorrelation is TransactionCorrelation.Invalid or TransactionCorrelation.Mismatch)
        {
            var eventType = transactionCorrelation == TransactionCorrelation.Invalid
                ? "protocol.invalid_transaction_id"
                : "protocol.transaction_mismatch";
            var detail = transactionCorrelation == TransactionCorrelation.Invalid
                ? "O transactionId informado pela máquina é inválido."
                : "O transactionId informado não pertence à sessão ativa da máquina.";
            await AddEventAsync(session, eventType, detail, null, message.DataJson, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            await BroadcastSessionAsync(session);
            return;
        }

        if (command == "pool" && type == "begin_session")
        {
            if (session.State == MachineSessionStates.Opening)
            {
                session.State = MachineSessionStates.AwaitingSelection;
                session.SelectionDeadlineAt = DateTime.UtcNow.AddMinutes(2);
            }
        }
        else if (command == "vend" && type == "request")
        {
            if (session.State != MachineSessionStates.AwaitingSelection)
            {
                await AddEventAsync(session, "protocol.invalid_vend_request", "Solicitação recebida fora de AwaitingSelection.", null, message.DataJson, cancellationToken);
            }
            else
            {
                var price = GetInt(data, "itemPrice");
                var item = GetInt(data, "itemNumber");
                if (price <= 0 || item < 0)
                {
                    await DenyWithoutPaymentAsync(session, "invalid_vend_request", cancellationToken);
                }
                else
                {
                    session.ItemNumber = item;
                    session.AmountCents = price;
                    session.SelectionDeadlineAt = null;
                    await _db.SaveChangesAsync(cancellationToken);
                    await CreateAndSendPixAsync(session, price, item, cancellationToken);
                }
            }
        }
        else if (command == "pool" && type == "vend_approved")
        {
            if (session.State == MachineSessionStates.PaymentApproved)
                session.State = MachineSessionStates.AwaitingDeliveryResult;
        }
        else if (command == "pool" && type == "vend_denied")
        {
            if (session.State == MachineSessionStates.DeniedAwaitingClosure)
                session.CloseReason ??= "payment_rejected";
        }
        else if (command == "vend" && type == "success")
        {
            await HandleDeliverySuccessAsync(session, data, cancellationToken);
        }
        else if (command == "vend" && type == "failure")
        {
            await HandleDeliveryFailureAsync(session, data, message.DataJson, cancellationToken);
        }
        else if (command == "vend" && type == "sessioncomplete")
        {
            if (session.State == MachineSessionStates.AwaitingSelection)
            {
                session.State = MachineSessionStates.DeviceClosingBeforeSelection;
                session.SelectionDeadlineAt = null;
                session.CloseReason = "device_closed_before_selection";
            }
        }
        else if (command == "pool" && type == "end_session")
        {
            await HandleEndSessionAsync(session, data, cancellationToken);
        }

        await _db.SaveChangesAsync(cancellationToken);
        await BroadcastSessionAsync(session);
    }

    public async Task HandlePaymentResultAsync(Guid transactionId, bool approved, string reason, CancellationToken cancellationToken)
    {
        var session = await _db.MachineSessions.Include(x => x.Machine).Include(x => x.Transaction)
            .FirstOrDefaultAsync(x => x.TransactionId == transactionId, cancellationToken);
        if (session == null || session.Transaction == null || session.Machine == null) return;

        if (session.State != MachineSessionStates.PaymentPending)
        {
            if (approved && session.State is MachineSessionStates.DeniedAwaitingClosure or MachineSessionStates.Denied)
            {
                session.Transaction.Status = "Approved";
                await AddEventAsync(session, "payment.approved_after_cancellation", reason, null, null, cancellationToken);
                await _db.SaveChangesAsync(cancellationToken);
                await BeginRefundAsync(session, "approved_after_cancellation", cancellationToken);
                return;
            }
            await AddEventAsync(session, "payment.duplicate_result", reason, null, null, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            return;
        }

        if (approved)
        {
            session.Transaction.Status = "Approved";
            session.Transaction.CompletedAt = DateTime.UtcNow;
            session.State = MachineSessionStates.PaymentApproved;
            session.PaymentDeadlineAt = null;
            session.DeliveryDeadlineAt = DateTime.UtcNow.AddSeconds(60);
            await AddEventAsync(session, "payment.approved", reason, null, null, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            var result = await SendTrackedCommandAsync(session.Machine, session, "VENDA_APROVADA", null, cancellationToken);
            if (!result.Success) await BeginRefundAsync(session, "approval_delivery_failed", cancellationToken);
        }
        else
        {
            session.Transaction.Status = "Rejected";
            session.Transaction.CompletedAt = DateTime.UtcNow;
            session.State = MachineSessionStates.DeniedAwaitingClosure;
            session.CloseReason = reason;
            session.PaymentDeadlineAt = null;
            await AddEventAsync(session, "payment.denied", reason, null, null, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            await SendTrackedCommandAsync(session.Machine, session, "VENDA_NEGADA", null, cancellationToken);
        }

        await _db.SaveChangesAsync(cancellationToken);
        await BroadcastSessionAsync(session);
    }

    public async Task ProcessDeadlinesAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var sessions = await ActiveSessions().Include(x => x.Machine).Include(x => x.Transaction)
            .Where(x => (x.SelectionDeadlineAt != null && x.SelectionDeadlineAt <= now) ||
                        (x.PaymentDeadlineAt != null && x.PaymentDeadlineAt <= now) ||
                        (x.DeliveryDeadlineAt != null && x.DeliveryDeadlineAt <= now) ||
                        ((x.State == MachineSessionStates.ClosingWithoutSelection ||
                          x.State == MachineSessionStates.DeviceClosingBeforeSelection ||
                          x.State == MachineSessionStates.DeniedAwaitingClosure ||
                          x.State == MachineSessionStates.DeliveryFailed ||
                          x.State == MachineSessionStates.RefundPending ||
                          x.State == MachineSessionStates.Refunded ||
                          x.State == MachineSessionStates.ReconciliationRequired ||
                          x.State == MachineSessionStates.Completed) && x.LastEventAt <= now.AddSeconds(-30)))
            .ToListAsync(cancellationToken);

        foreach (var session in sessions)
        {
            if (session.SelectionDeadlineAt <= now && session.State == MachineSessionStates.AwaitingSelection)
            {
                await CancelSessionAsync(session.Id, session.CompanyId, null, "selection_timeout", cancellationToken);
            }
            else if (session.PaymentDeadlineAt <= now && session.State == MachineSessionStates.PaymentPending)
            {
                await CancelPendingPaymentAsync(session, "payment_timeout", null, cancellationToken);
            }
            else if (session.DeliveryDeadlineAt <= now && session.State is MachineSessionStates.PaymentApproved or MachineSessionStates.AwaitingDeliveryResult)
            {
                await BeginRefundAsync(session, "delivery_timeout", cancellationToken);
            }
            else if (session.LastEventAt <= now.AddSeconds(-30))
            {
                session.ClosedAt = now;
                session.CloseReason ??= "missing_end_session";
                if (session.State is MachineSessionStates.ClosingWithoutSelection or
                    MachineSessionStates.DeviceClosingBeforeSelection or MachineSessionStates.DeniedAwaitingClosure)
                    session.State = MachineSessionStates.ClosureIncomplete;
                await AddEventAsync(session, "session.closure_incomplete", session.CloseReason, null, null, cancellationToken);
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task ProcessOutboxAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var messages = await _db.OutboxMessages
            .Where(x => x.Status == "pending" && (x.NextAttemptAt == null || x.NextAttemptAt <= now))
            .OrderBy(x => x.CreatedAt)
            .Take(25)
            .ToListAsync(cancellationToken);

        foreach (var message in messages)
        {
            var transaction = message.TransactionId.HasValue
                ? await _db.PaymentTransactions.FirstOrDefaultAsync(x => x.Id == message.TransactionId, cancellationToken)
                : null;
            var session = message.SessionId.HasValue
                ? await _db.MachineSessions.FirstOrDefaultAsync(x => x.Id == message.SessionId, cancellationToken)
                : null;
            if (transaction == null)
            {
                message.Status = "failed";
                message.LastError = "transaction_not_found";
                continue;
            }

            var success = message.MessageType switch
            {
                "refund_payment" => await RefundProviderPaymentAsync(transaction, cancellationToken),
                "cancel_payment" => await CancelProviderPaymentAsync(transaction, cancellationToken),
                _ => true
            };

            message.Attempts++;
            if (success)
            {
                message.Status = "processed";
                message.ProcessedAt = now;
                if (message.MessageType == "refund_payment" && session != null)
                {
                    session.State = MachineSessionStates.Refunded;
                    transaction.Status = "Refunded";
                    await AddEventAsync(session, "refund.completed", null, null, null, cancellationToken);
                    await BroadcastSessionAsync(session);
                }
            }
            else if (message.Attempts >= 5)
            {
                message.Status = "failed";
                message.LastError = "provider_operation_failed";
                if (message.MessageType == "refund_payment" && session != null)
                {
                    session.State = MachineSessionStates.ReconciliationRequired;
                    transaction.Status = "Failed";
                    await AddEventAsync(session, "refund.failed", "Estorno falhou após cinco tentativas.", null, null, cancellationToken);
                    await BroadcastSessionAsync(session);
                }
            }
            else
            {
                message.LastError = "provider_operation_failed";
                message.NextAttemptAt = now.AddSeconds(Math.Min(300, 5 * Math.Pow(2, message.Attempts)));
            }
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task CreateAndSendPixAsync(MachineSession session, int amountCents, int itemNumber, CancellationToken cancellationToken)
    {
        var machine = session.Machine ?? await _db.Machines.FirstAsync(x => x.Id == session.MachineId, cancellationToken);
        if (!machine.MercadoPagoEnabled)
        {
            await DenyWithoutPaymentAsync(session, "mercado_pago_disabled", cancellationToken);
            return;
        }

        var existing = session.TransactionId.HasValue
            ? await _db.PaymentTransactions.FirstOrDefaultAsync(x => x.Id == session.TransactionId.Value, cancellationToken)
            : null;
        if (existing != null)
        {
            await SendPixPayloadAsync(machine, session, existing, cancellationToken);
            return;
        }

        var amount = amountCents / 100m;
        var settings = await _db.SystemSettings.FirstOrDefaultAsync(cancellationToken);
        var feePercent = settings?.ApplicationFeePercent ?? 5m;
        var fee = Math.Round(amount * feePercent / 100m, 2);
        var transaction = new PaymentTransaction
        {
            MachineId = machine.Id,
            CompanyId = session.CompanyId,
            Amount = amount,
            ApplicationFee = fee,
            Status = "Pending",
            SendTelemetryToMachine = true
        };
        _db.PaymentTransactions.Add(transaction);
        session.TransactionId = transaction.Id;
        session.State = MachineSessionStates.PaymentPending;
        session.PaymentDeadlineAt = DateTime.UtcNow.AddMinutes(5);
        await _db.SaveChangesAsync(cancellationToken);

        try
        {
            var integration = await _db.MercadoPagoIntegrations.FirstOrDefaultAsync(
                x => x.CompanyId == session.CompanyId && x.IsActive, cancellationToken)
                ?? throw new InvalidOperationException("Integração Mercado Pago não configurada.");
            var accessToken = EncryptionService.Decrypt(integration.AccessToken);
            if (_environment.IsDevelopment() || accessToken.Contains("mock", StringComparison.OrdinalIgnoreCase) || accessToken.Length < 15)
            {
                transaction.MercadoPagoPaymentId = $"mock_mp_{Guid.NewGuid():N}";
                transaction.MercadoPagoStatus = "pending";
                transaction.MercadoPagoStatusDetail = "pending_waiting_transfer";
                transaction.QrCode = $"000201-vendmachine-{transaction.Id:N}";
            }
            else
            {
                var client = _httpClientFactory.CreateClient("MercadoPago");
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                client.DefaultRequestHeaders.Add("X-Idempotency-Key", transaction.Id.ToString());
                var payload = new
                {
                    transaction_amount = amount,
                    description = $"Venda item {itemNumber} - {machine.Name}",
                    payment_method_id = "pix",
                    payer = new { email = "comprador@vendmachine.com.br", first_name = "Cliente", last_name = "Vending" },
                    application_fee = fee,
                    notification_url = _configuration["MercadoPago:WebhookUrl"],
                    external_reference = transaction.Id.ToString()
                };
                var response = await client.PostAsJsonAsync("https://api.mercadopago.com/v1/payments", payload, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                transaction.RawResponse = body;
                response.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                transaction.MercadoPagoPaymentId = root.GetProperty("id").GetRawText();
                transaction.MercadoPagoStatus = GetString(root, "status");
                transaction.MercadoPagoStatusDetail = GetString(root, "status_detail");
                var txData = root.GetProperty("point_of_interaction").GetProperty("transaction_data");
                transaction.QrCode = GetString(txData, "qr_code") ?? string.Empty;
                transaction.QrCodeBase64 = GetString(txData, "qr_code_base64") ?? string.Empty;
            }

            await AddEventAsync(session, "payment.pix_created", $"Pix criado para o item {itemNumber}.", null, null, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            await SendPixPayloadAsync(machine, session, transaction, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao criar Pix para a sessão {SessionId}.", session.Id);
            transaction.Status = "Failed";
            transaction.RawResponse ??= JsonSerializer.Serialize(new { error = ex.Message });
            await DenyWithoutPaymentAsync(session, "provider_error", cancellationToken);
        }

        await _db.SaveChangesAsync(cancellationToken);
        await BroadcastSessionAsync(session);
    }

    private async Task SendPixPayloadAsync(Machine machine, MachineSession session, PaymentTransaction transaction, CancellationToken cancellationToken)
    {
        var payload = new
        {
            command = "pay",
            type = "pix",
            code = transaction.QrCode,
            transactionId = transaction.Id,
            amount = session.AmountCents,
            expiresAt = session.PaymentDeadlineAt
        };
        var result = await SendTrackedCommandAsync(machine, session, payload, null, cancellationToken);
        if (!result.Success) await CancelPendingPaymentAsync(session, "machine_offline", null, cancellationToken);
    }

    private async Task HandleDeliverySuccessAsync(MachineSession session, JsonElement data, CancellationToken cancellationToken)
    {
        if (session.State is MachineSessionStates.PaymentApproved or MachineSessionStates.AwaitingDeliveryResult)
        {
            session.State = MachineSessionStates.Completed;
            session.DeliveryDeadlineAt = null;
            if (session.Transaction != null) session.Transaction.Status = "Approved";
            await AddEventAsync(session, "delivery.success", "Entrega confirmada pela máquina.", null, data.GetRawText(), cancellationToken);
        }
        else if (session.State is MachineSessionStates.DeliveryFailed or MachineSessionStates.RefundPending or MachineSessionStates.Refunded)
        {
            await AddEventAsync(session, "delivery.conflicting_result", "Success recebido depois de failure.", null, data.GetRawText(), cancellationToken);
        }
    }

    private async Task HandleDeliveryFailureAsync(MachineSession session, JsonElement data, string rawData, CancellationToken cancellationToken)
    {
        if (session.State is MachineSessionStates.PaymentApproved or MachineSessionStates.AwaitingDeliveryResult)
        {
            var original = GetString(data, "reason") ?? "unknown";
            var normalized = original is "out_of_cup" or "out_of_product" or "mechanism_error" ? original : "unknown";
            if (session.TransactionId.HasValue && !await _db.DeliveryFailures.AnyAsync(x => x.TransactionId == session.TransactionId, cancellationToken))
            {
                _db.DeliveryFailures.Add(new DeliveryFailure
                {
                    CompanyId = session.CompanyId,
                    MachineId = session.MachineId,
                    SessionId = session.Id,
                    TransactionId = session.TransactionId.Value,
                    Reason = normalized,
                    OriginalReason = original
                });
            }
            session.State = MachineSessionStates.DeliveryFailed;
            session.DeliveryDeadlineAt = null;
            await AddEventAsync(session, "delivery.failure", normalized, null, rawData, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            await BeginRefundAsync(session, $"delivery_failure:{normalized}", cancellationToken);
        }
        else if (session.State == MachineSessionStates.Completed)
        {
            await AddEventAsync(session, "delivery.conflicting_result", "Failure recebido depois de success.", null, rawData, cancellationToken);
        }
    }

    private async Task HandleEndSessionAsync(MachineSession session, JsonElement data, CancellationToken cancellationToken)
    {
        if (session.State == MachineSessionStates.ClosingWithoutSelection)
            session.State = MachineSessionStates.ClosedWithoutSelection;
        else if (session.State is MachineSessionStates.Opening or MachineSessionStates.DeviceClosingBeforeSelection or MachineSessionStates.AwaitingSelection)
        {
            session.State = MachineSessionStates.ClosedByDeviceBeforeSelection;
            session.CloseReason = "device_closed_before_selection";
            await EvaluateEarlyCloseAlertAsync(session, cancellationToken);
        }
        else if (session.State == MachineSessionStates.DeniedAwaitingClosure)
            session.State = MachineSessionStates.Denied;
        else if (session.State == MachineSessionStates.PaymentPending)
        {
            await CancelPendingPaymentAsync(
                session,
                "device_closed_during_payment",
                null,
                cancellationToken,
                notifyMachine: false);
            session.State = MachineSessionStates.Denied;
        }
        else if (session.State is MachineSessionStates.PaymentApproved or MachineSessionStates.AwaitingDeliveryResult)
        {
            session.CloseReason ??= "delivery_result_missing";
            session.DeliveryDeadlineAt = null;
            await AddEventAsync(
                session,
                "delivery.result_missing",
                "A máquina encerrou a sessão sem informar success ou failure.",
                null,
                data.GetRawText(),
                cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            await BeginRefundAsync(session, "delivery_result_missing", cancellationToken);
        }

        session.ClosedAt = DateTime.UtcNow;
        session.SelectionDeadlineAt = null;
        session.PaymentDeadlineAt = null;
        session.DeliveryDeadlineAt = null;
    }

    private async Task EvaluateEarlyCloseAlertAsync(MachineSession session, CancellationToken cancellationToken)
    {
        var window = DateTime.UtcNow.AddMinutes(-15);
        var count = await _db.MachineSessions.CountAsync(x => x.MachineId == session.MachineId &&
            x.CloseReason == "device_closed_before_selection" && x.ClosedAt >= window, cancellationToken) + 1;
        if (count < 3) return;
        var cooldown = DateTime.UtcNow.AddMinutes(-30);
        var hasRecentAlert = await _db.TelemetryEvents.AnyAsync(x => x.MachineId == session.MachineId &&
            x.EventType == "alert.repeated_early_close" && x.CreatedAt >= cooldown, cancellationToken);
        if (!hasRecentAlert)
            await AddEventAsync(session, "alert.repeated_early_close", $"{count} encerramentos antes da seleção em 15 minutos.", null, null, cancellationToken);
    }

    private async Task CancelPendingPaymentAsync(
        MachineSession session,
        string reason,
        Guid? userId,
        CancellationToken cancellationToken,
        bool notifyMachine = true)
    {
        session.State = MachineSessionStates.DeniedAwaitingClosure;
        session.CloseReason = reason;
        session.PaymentDeadlineAt = null;
        if (session.Transaction != null)
        {
            session.Transaction.Status = "Rejected";
            session.Transaction.CompletedAt = DateTime.UtcNow;
            EnqueueOutbox(session, "cancel_payment", new { reason });
        }
        await AddEventAsync(session, "payment.cancelled", reason, userId, null, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        if (notifyMachine && session.Machine != null)
            await SendTrackedCommandAsync(session.Machine, session, "VENDA_NEGADA", userId, cancellationToken);
    }

    private async Task DenyWithoutPaymentAsync(MachineSession session, string reason, CancellationToken cancellationToken)
    {
        session.State = MachineSessionStates.DeniedAwaitingClosure;
        session.CloseReason = reason;
        session.SelectionDeadlineAt = null;
        await AddEventAsync(session, "sale.denied", reason, null, null, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        var machine = session.Machine ?? await _db.Machines.FirstAsync(x => x.Id == session.MachineId, cancellationToken);
        await SendTrackedCommandAsync(machine, session, "VENDA_NEGADA", null, cancellationToken);
    }

    private async Task BeginRefundAsync(MachineSession session, string reason, CancellationToken cancellationToken)
    {
        if (session.Transaction == null && session.TransactionId.HasValue)
            session.Transaction = await _db.PaymentTransactions.FirstOrDefaultAsync(x => x.Id == session.TransactionId, cancellationToken);
        if (session.Transaction == null) return;
        if (session.Transaction.Status is "Refunded" or "RefundPending") return;

        session.State = MachineSessionStates.RefundPending;
        session.Transaction.Status = "RefundPending";
        session.DeliveryDeadlineAt = null;
        await AddEventAsync(session, "refund.requested", reason, null, null, cancellationToken);
        EnqueueOutbox(session, "refund_payment", new { reason });
        await _db.SaveChangesAsync(cancellationToken);
        await ProcessOutboxAsync(cancellationToken);
        await BroadcastSessionAsync(session);
    }

    private async Task<bool> RefundProviderPaymentAsync(PaymentTransaction transaction, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(transaction.MercadoPagoPaymentId) || transaction.MercadoPagoPaymentId.StartsWith("mock_")) return true;
        try
        {
            var integration = await _db.MercadoPagoIntegrations.FirstOrDefaultAsync(x => x.CompanyId == transaction.CompanyId && x.IsActive, cancellationToken);
            if (integration == null) return false;
            var client = _httpClientFactory.CreateClient("MercadoPago");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", EncryptionService.Decrypt(integration.AccessToken));
            
            client.DefaultRequestHeaders.Add("X-Idempotency-Key", $"refund-{transaction.Id}");
            var body = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            
            var response = await client.PostAsync($"https://api.mercadopago.com/v1/payments/{transaction.MercadoPagoPaymentId}/refunds", body, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao estornar transação {TransactionId}.", transaction.Id);
            return false;
        }
    }

    private async Task<bool> CancelProviderPaymentAsync(PaymentTransaction transaction, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(transaction.MercadoPagoPaymentId) || transaction.MercadoPagoPaymentId.StartsWith("mock_")) return true;
        try
        {
            var integration = await _db.MercadoPagoIntegrations.FirstOrDefaultAsync(x => x.CompanyId == transaction.CompanyId && x.IsActive, cancellationToken);
            if (integration == null) return false;
            var client = _httpClientFactory.CreateClient("MercadoPago");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", EncryptionService.Decrypt(integration.AccessToken));
            var response = await client.PutAsJsonAsync($"https://api.mercadopago.com/v1/payments/{transaction.MercadoPagoPaymentId}", new { status = "cancelled" }, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao cancelar transação {TransactionId}.", transaction.Id);
            return false;
        }
    }

    private void EnqueueOutbox(MachineSession session, string messageType, object payload)
    {
        if (_db.OutboxMessages.Local.Any(x => x.SessionId == session.Id && x.MessageType == messageType && x.Status == "pending")) return;
        _db.OutboxMessages.Add(new OutboxMessage
        {
            CompanyId = session.CompanyId,
            MachineId = session.MachineId,
            SessionId = session.Id,
            TransactionId = session.TransactionId,
            MessageType = messageType,
            PayloadJson = JsonSerializer.Serialize(payload),
            NextAttemptAt = DateTime.UtcNow
        });
    }

    private async Task<DeviceCommandResult> SendTrackedCommandAsync(
        Machine machine,
        MachineSession session,
        object command,
        Guid? userId,
        CancellationToken cancellationToken)
    {
        var commandName = command is string text ? text : JsonSerializer.Serialize(command);
        var result = await _connections.SendCommandAsync(machine.SerialNumber, command, cancellationToken);
        _db.TelemetryCommands.Add(new TelemetryCommand
        {
            CompanyId = session.CompanyId,
            MachineId = session.MachineId,
            SessionId = session.Id,
            TransactionId = session.TransactionId,
            UserId = userId,
            MessageId = result.MessageId,
            Command = commandName,
            DataJson = command is string ? null : commandName,
            Status = result.Success ? "acknowledged" : "failed",
            Attempts = result.Attempts,
            SentAt = DateTime.UtcNow,
            AckAt = result.Success ? DateTime.UtcNow : null,
            Error = result.Error
        });
        await _db.SaveChangesAsync(cancellationToken);
        return result;
    }

    private Task AddEventAsync(MachineSession session, string type, string? detail, Guid? userId, string? dataJson, CancellationToken _)
    {
        _db.TelemetryEvents.Add(new TelemetryEvent
        {
            CompanyId = session.CompanyId,
            MachineId = session.MachineId,
            SessionId = session.Id,
            TransactionId = session.TransactionId,
            UserId = userId,
            EventType = type,
            Detail = detail,
            DataJson = dataJson
        });
        return Task.CompletedTask;
    }

    private Task AddStandaloneEventAsync(InboundDeviceMessage message, string type, string detail, string? dataJson, CancellationToken _)
    {
        _db.TelemetryEvents.Add(new TelemetryEvent
        {
            CompanyId = message.CompanyId,
            MachineId = message.MachineId,
            EventType = type,
            Detail = detail,
            DataJson = dataJson
        });
        return _db.SaveChangesAsync();
    }

    private Task BroadcastSessionAsync(MachineSession session) => _panelHub.BroadcastCompanyAsync(session.CompanyId, new
    {
        type = "sale.updated",
        machineId = session.MachineId,
        sessionId = session.Id,
        transactionId = session.TransactionId,
        state = session.State,
        itemNumber = session.ItemNumber,
        amountCents = session.AmountCents,
        closeReason = session.CloseReason,
        selectionDeadlineAt = session.SelectionDeadlineAt,
        paymentDeadlineAt = session.PaymentDeadlineAt,
        deliveryDeadlineAt = session.DeliveryDeadlineAt,
        closedAt = session.ClosedAt,
        updatedAt = session.LastEventAt
    });

    private IQueryable<MachineSession> ActiveSessions() =>
        _db.MachineSessions.Where(x => x.ClosedAt == null);

    private static TransactionCorrelation GetTransactionCorrelation(MachineSession session, JsonElement data)
    {
        if (!data.TryGetProperty("transactionId", out var transactionId) ||
            transactionId.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return TransactionCorrelation.Missing;

        if (transactionId.ValueKind != JsonValueKind.String)
            return TransactionCorrelation.Invalid;

        var value = transactionId.GetString();
        if (string.IsNullOrWhiteSpace(value))
            return TransactionCorrelation.Missing;

        if (!Guid.TryParse(value, out var parsed))
            return TransactionCorrelation.Invalid;

        return session.TransactionId.HasValue && parsed == session.TransactionId.Value
            ? TransactionCorrelation.Matched
            : TransactionCorrelation.Mismatch;
    }

    private enum TransactionCorrelation
    {
        Missing,
        Matched,
        Invalid,
        Mismatch
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int GetInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var parsed) ? parsed : -1;
}
