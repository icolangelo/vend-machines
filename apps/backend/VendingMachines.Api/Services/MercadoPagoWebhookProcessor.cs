using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;

namespace VendingMachines.Api.Services;

public sealed class MercadoPagoWebhookProcessor
{
    private const string ApprovalAfterFinancialReversalReason = "provider_approval_after_financial_reversal";
    private const string ApprovalAfterNegativeTerminalReason = "provider_approval_after_rejected_or_cancelled";
    private const string ReimbursementAmountMissingReason = "chargeback_reimbursement_amount_missing";
    private const string ProviderRefundAmountOutOfRangeReason = "provider_refund_amount_out_of_range";
    private const string ProviderInMediationReason = "provider_in_mediation";
    private const string RegressionAfterApprovedReason = "provider_regression_after_approved";
    private const string RegressionAfterRefundedReason = "provider_regression_after_refunded";
    private const string RegressionAfterChargebackReason = "provider_regression_after_chargeback";
    private const string ManualChargebackReimbursementConfirmedReason = "manual_chargeback_reimbursement_confirmed";

    private const int MaxAttempts = 12;
    private readonly AppDbContext _db;
    private readonly SessionOrchestrator _sessionOrchestrator;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<MercadoPagoWebhookProcessor> _logger;

    public MercadoPagoWebhookProcessor(
        AppDbContext db,
        SessionOrchestrator sessionOrchestrator,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<MercadoPagoWebhookProcessor> logger)
    {
        _db = db;
        _sessionOrchestrator = sessionOrchestrator;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var legacyOwnerPaymentIds = GetLegacyOwnerPaymentIds(now);
        var candidate = await _db.WebhookInboxMessages
            .AsNoTracking()
            .Where(message =>
                ((message.Status == "Pending" || message.Status == "Retry") &&
                 (!message.NextAttemptAt.HasValue || message.NextAttemptAt <= now)) ||
                (message.Status == "Processing" && message.ProcessingStartedAt < now.AddMinutes(-2)))
            .Where(message => !legacyOwnerPaymentIds.Contains(message.PaymentId))
            .OrderBy(message => message.ReceivedAt)
            .Select(message => new { message.Id, message.Status, message.ProcessingStartedAt })
            .FirstOrDefaultAsync(cancellationToken);

        if (candidate is null)
        {
            return false;
        }

        var claimedAt = now;
        var staleProcessingCutoff = claimedAt.AddMinutes(-2);
        int claimed;
        try
        {
            claimed = await _db.WebhookInboxMessages.Where(message => message.Id == candidate.Id &&
                    !GetLegacyOwnerPaymentIds(claimedAt).Contains(message.PaymentId) &&
                    ((message.Status == "Pending" || message.Status == "Retry") &&
                     (!message.NextAttemptAt.HasValue || message.NextAttemptAt <= claimedAt) ||
                     (message.Status == "Processing" && message.ProcessingStartedAt < staleProcessingCutoff)))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(message => message.Status, "Processing")
                    .SetProperty(message => message.ProcessingStartedAt, claimedAt)
                    .SetProperty(message => message.Attempts, message => message.Attempts + 1), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ReleaseClaimAfterShutdownAsync(candidate.Id, claimedAt);
            throw;
        }

        if (claimed == 0)
        {
            return true;
        }

        try
        {
            await ProcessPaymentAsync(candidate.Id, cancellationToken);
            var completed = await _db.WebhookInboxMessages
                .Where(message => message.Id == candidate.Id &&
                    message.Status == "Processing" && message.ProcessingStartedAt == claimedAt)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(message => message.Status, "Processed")
                    .SetProperty(message => message.ProcessedAt, DateTime.UtcNow)
                    .SetProperty(message => message.ProcessingStartedAt, (DateTime?)null)
                    .SetProperty(message => message.NextAttemptAt, (DateTime?)null)
                    .SetProperty(message => message.LastError, (string?)null), cancellationToken);
            if (completed == 0)
                _logger.LogWarning("Worker Mercado Pago perdeu o lease do webhook {WebhookInboxId}; a conclusão desta tentativa não alterou o estado atual.", candidate.Id);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ReleaseClaimAfterShutdownAsync(candidate.Id, claimedAt);
            throw;
        }
        catch (Exception exception)
        {
            try
            {
                var attempts = await _db.WebhookInboxMessages
                    .Where(message => message.Id == candidate.Id &&
                        message.Status == "Processing" && message.ProcessingStartedAt == claimedAt)
                    .Select(message => (int?)message.Attempts)
                    .SingleOrDefaultAsync(cancellationToken);
                if (!attempts.HasValue)
                {
                    _logger.LogWarning(exception,
                        "Worker Mercado Pago perdeu o lease do webhook {WebhookInboxId}; a falha desta tentativa não alterou o estado atual.", candidate.Id);
                    return true;
                }

                var isFinal = attempts.Value >= MaxAttempts;
                var delaySeconds = Math.Min(3600, 5 * Math.Pow(2, Math.Min(attempts.Value - 1, 9)));
                var safeError = exception.GetType().Name;
                var updated = await _db.WebhookInboxMessages
                    .Where(message => message.Id == candidate.Id &&
                        message.Status == "Processing" && message.ProcessingStartedAt == claimedAt)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(message => message.Status, isFinal ? "Failed" : "Retry")
                        .SetProperty(message => message.ProcessingStartedAt, (DateTime?)null)
                        .SetProperty(message => message.NextAttemptAt, isFinal ? (DateTime?)null : DateTime.UtcNow.AddSeconds(delaySeconds))
                        .SetProperty(message => message.LastError, safeError), cancellationToken);

                if (updated == 0)
                {
                    _logger.LogWarning(exception,
                        "Worker Mercado Pago perdeu o lease do webhook {WebhookInboxId}; a falha desta tentativa não alterou o estado atual.", candidate.Id);
                }
                else
                {
                    _logger.LogError(exception,
                        "Falha no processamento do webhook {WebhookInboxId}; tentativa {Attempt}/{MaxAttempts}; estado {Status}.",
                        candidate.Id, attempts.Value, MaxAttempts, isFinal ? "Failed" : "Retry");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await ReleaseClaimAfterShutdownAsync(candidate.Id, claimedAt);
                throw;
            }
        }

        return true;
    }

    private async Task ReleaseClaimAfterShutdownAsync(Guid inboxId, DateTime claimedAt)
    {
        try
        {
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var retryAt = DateTime.UtcNow;
            var released = await _db.WebhookInboxMessages
                .Where(message => message.Id == inboxId &&
                    message.Status == "Processing" && message.ProcessingStartedAt == claimedAt)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(message => message.Status, "Retry")
                    .SetProperty(message => message.ProcessingStartedAt, (DateTime?)null)
                    .SetProperty(message => message.NextAttemptAt, (DateTime?)retryAt)
                    .SetProperty(message => message.Attempts, message => message.Attempts > 0 ? message.Attempts - 1 : 0)
                    .SetProperty(message => message.LastError, (string?)null), cleanupTimeout.Token);

            if (released > 0)
                _logger.LogInformation("Webhook {WebhookInboxId} liberado para retomada imediata após cancelamento do worker.", inboxId);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Não foi possível liberar o lease do webhook {WebhookInboxId} durante o encerramento; ele será retomado após expiração do lease. Tipo de erro: {ExceptionType}.",
                inboxId,
                exception.GetType().Name);
        }
    }

    public async Task ReconcilePendingPaymentsAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var eligibleBefore = now.AddMinutes(-2);
        var lastReconciledBefore = now.AddMinutes(-5);
        var terminalReconciledBefore = now.AddHours(-1);
        var rejectedReconciledBefore = now.AddHours(-24);
        var legacyOwnerPaymentIds = GetLegacyOwnerPaymentIds(now);
        var transactionIds = await _db.PaymentTransactions
            .AsNoTracking()
            .Where(transaction =>
                transaction.MercadoPagoPaymentId != null &&
                !legacyOwnerPaymentIds.Contains(transaction.MercadoPagoPaymentId) &&
                !transaction.MercadoPagoPaymentId.Contains("mock") &&
                ((transaction.Status == "Pending" && transaction.CreatedAt <= eligibleBefore &&
                  (!transaction.LastReconciliationAt.HasValue || transaction.LastReconciliationAt <= lastReconciledBefore)) ||
                 ((transaction.Status == "Approved" || transaction.Status == "Refunded" ||
                   transaction.Status == "Chargeback" || transaction.Status == "ReconciliationRequired") &&
                  (!transaction.LastReconciliationAt.HasValue || transaction.LastReconciliationAt <= terminalReconciledBefore)) ||
                 (transaction.Status == "Rejected" &&
                  (!transaction.LastReconciliationAt.HasValue || transaction.LastReconciliationAt <= rejectedReconciledBefore))))
            .OrderBy(transaction => transaction.LastReconciliationAt)
            .ThenBy(transaction => transaction.CreatedAt)
            .Take(50)
            .Select(transaction => new { transaction.Id, transaction.Status })
            .ToListAsync(cancellationToken);

        foreach (var candidate in transactionIds)
        {
            var claimedAt = DateTime.UtcNow;
            var claimBefore = candidate.Status switch
            {
                "Pending" => claimedAt.AddMinutes(-5),
                "Rejected" => claimedAt.AddHours(-24),
                _ => claimedAt.AddHours(-1)
            };
            var claimed = await _db.PaymentTransactions
                .Where(transaction => transaction.Id == candidate.Id &&
                    transaction.Status == candidate.Status &&
                    transaction.MercadoPagoPaymentId != null &&
                    !GetLegacyOwnerPaymentIds(claimedAt).Contains(transaction.MercadoPagoPaymentId) &&
                    (candidate.Status != "Pending" || transaction.CreatedAt <= eligibleBefore) &&
                    (!transaction.LastReconciliationAt.HasValue || transaction.LastReconciliationAt <= claimBefore))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(transaction => transaction.LastReconciliationAt, claimedAt), cancellationToken);

            if (claimed == 0)
            {
                continue;
            }

            try
            {
                var paymentId = await _db.PaymentTransactions.AsNoTracking()
                    .Where(transaction => transaction.Id == candidate.Id)
                    .Select(transaction => transaction.MercadoPagoPaymentId)
                    .SingleAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(paymentId))
                {
                    await ProcessPaymentByIdAsync(paymentId, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception,
                    "Reconciliação automática falhou para transação {PaymentTransactionId}; nova tentativa após o intervalo configurado.",
                    candidate.Id);
            }
            finally
            {
                // A falha de uma cobrança não pode deixar entidades modificadas no
                // contexto e persistí-las acidentalmente durante a próxima tentativa.
                _db.ChangeTracker.Clear();
            }
        }
    }

    private async Task ProcessPaymentAsync(Guid inboxId, CancellationToken cancellationToken)
    {
        var inbox = await _db.WebhookInboxMessages.AsNoTracking()
            .SingleAsync(message => message.Id == inboxId, cancellationToken);
        await ProcessPaymentByIdAsync(inbox.PaymentId, cancellationToken);
    }

    private IQueryable<string> GetLegacyOwnerPaymentIds(DateTime now)
    {
        var connectionFreshnessCutoff = now - MachineConnectionFreshness.Window;
        return _db.PaymentTransactions.AsNoTracking()
            .Where(transaction => transaction.MercadoPagoPaymentId != null &&
                _db.MachineSessions.AsNoTracking().Any(session =>
                    session.TransactionId == transaction.Id && session.ClosedAt == null &&
                    _db.MachineConnectionStates.AsNoTracking().Any(state =>
                        state.MachineId == session.MachineId &&
                        state.IsOnline && state.LastSeenAt >= connectionFreshnessCutoff &&
                        (state.OwnerNodeId == null || state.OwnerNodeId == "" ||
                         state.ConnectionId == null || state.ConnectionId == "" ||
                         state.OwnerConnectionId != state.ConnectionId ||
                         state.CommandRoutingVersion != TelemetryConnectionManager.CurrentCommandRoutingVersion ||
                         state.OwnerLeaseExpiresAt == null || state.OwnerLeaseExpiresAt <= now))))
            .Select(transaction => transaction.MercadoPagoPaymentId!);
    }

    private async Task ProcessPaymentByIdAsync(string paymentId, CancellationToken cancellationToken)
    {
        PaymentTransaction? transaction = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            transaction = await _db.PaymentTransactions
                .Include(payment => payment.Machine)
                .FirstOrDefaultAsync(payment => payment.MercadoPagoPaymentId == paymentId, cancellationToken);
            if (transaction is not null)
            {
                break;
            }

            if (attempt < 4)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                _db.ChangeTracker.Clear();
            }
        }

        if (transaction is null)
        {
            throw new InvalidOperationException($"Transação não encontrada para PaymentId '{paymentId}'.");
        }

        var previousTransactionStatus = transaction.Status;

        var integration = await _db.MercadoPagoIntegrations
            .FirstOrDefaultAsync(item => item.CompanyId == transaction.CompanyId && item.IsActive, cancellationToken);
        if (integration is null)
        {
            throw new InvalidOperationException($"Integração Mercado Pago ativa não encontrada para empresa {transaction.CompanyId}.");
        }

        var accessToken = await EnsureValidAccessTokenAsync(integration, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.mercadopago.com/v1/payments/{Uri.EscapeDataString(paymentId)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await _httpClientFactory.CreateClient("MercadoPago").SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Consulta Mercado Pago falhou com HTTP {(int)response.StatusCode}.");
        }

        using var json = JsonDocument.Parse(responseBody);
        var root = json.RootElement;
        var paymentStatus = root.TryGetProperty("status", out var statusProperty)
            ? statusProperty.GetString() ?? "pending"
            : "pending";
        var statusDetail = root.TryGetProperty("status_detail", out var detailProperty)
            ? detailProperty.GetString()
            : null;
        var hasRefundedAmount = TryReadDecimal(root, "transaction_amount_refunded", out var refundedAmount);
        transaction.MercadoPagoStatus = paymentStatus;
        transaction.MercadoPagoStatusDetail = statusDetail;
        var approved = paymentStatus == "approved";
        var rejected = paymentStatus is "rejected" or "cancelled";
        var isDisputed = paymentStatus is "charged_back" or "in_mediation";
        var isFullyRefunded = paymentStatus == "refunded" ||
            (paymentStatus == "charged_back" && statusDetail == "settled");
        var isUnsettled = paymentStatus is "pending" or "in_process" or "authorized";
        var isKnownProviderStatus = approved || rejected || isDisputed || paymentStatus == "refunded" || isUnsettled;
        var sale = await _db.SaleRecords.FirstOrDefaultAsync(
            record => record.TransactionId == transaction.Id, cancellationToken);
        var hasRefundAmountConflict = hasRefundedAmount &&
            (refundedAmount < 0m || refundedAmount > transaction.Amount ||
             sale != null && (refundedAmount > sale.GrossAmount ||
                 approved && refundedAmount >= sale.GrossAmount) ||
             approved && refundedAmount >= transaction.Amount);
        var previousReconciliationReason = transaction.ReconciliationReason;
        var manuallyConfirmedChargebackReimbursement =
            previousTransactionStatus == "Approved" &&
            previousReconciliationReason == ManualChargebackReimbursementConfirmedReason &&
            paymentStatus == "charged_back" && statusDetail == "reimbursed";
        var isConflictingApproval = approved &&
            (previousTransactionStatus is "Refunded" or "Chargeback" ||
             previousTransactionStatus is "Rejected" or "Cancelled" ||
             previousTransactionStatus == "ReconciliationRequired" &&
             previousReconciliationReason is ApprovalAfterFinancialReversalReason or ApprovalAfterNegativeTerminalReason);
        var isPersistentFinancialRegression = previousTransactionStatus == "ReconciliationRequired" &&
            (previousReconciliationReason switch
            {
                RegressionAfterRefundedReason => paymentStatus != "refunded",
                RegressionAfterChargebackReason => paymentStatus != "charged_back",
                RegressionAfterApprovedReason => !(approved || paymentStatus is ("refunded" or "charged_back")),
                _ => false
            });
        var requiresReconciliation =
            hasRefundAmountConflict ||
            isPersistentFinancialRegression ||
            isConflictingApproval && previousTransactionStatus is ("Rejected" or "Cancelled") ||
            (rejected || isUnsettled || !isKnownProviderStatus || isConflictingApproval) &&
            previousTransactionStatus is ("Approved" or "Refunded" or "Chargeback" or "ReconciliationRequired");
        if (requiresReconciliation)
        {
            transaction.Status = "ReconciliationRequired";
            transaction.ReconciliationReason ??= hasRefundAmountConflict
                ? ProviderRefundAmountOutOfRangeReason
                : isConflictingApproval
                ? previousTransactionStatus is "Rejected" or "Cancelled"
                    ? ApprovalAfterNegativeTerminalReason
                    : ApprovalAfterFinancialReversalReason
                : previousTransactionStatus switch
                {
                    "Approved" => RegressionAfterApprovedReason,
                    "Refunded" => RegressionAfterRefundedReason,
                    "Chargeback" => RegressionAfterChargebackReason,
                    _ => previousReconciliationReason
                };
        }
        else if (approved)
        {
            transaction.Status = "Approved";
            transaction.CompletedAt ??= DateTime.UtcNow;
            transaction.ReconciliationReason = null;
        }
        else if (paymentStatus == "refunded")
        {
            transaction.Status = "Refunded";
            transaction.CompletedAt ??= DateTime.UtcNow;
            transaction.ReconciliationReason = null;
        }
        else if (paymentStatus == "charged_back")
        {
            transaction.Status = statusDetail == "reimbursed"
                ? hasRefundedAmount || manuallyConfirmedChargebackReimbursement ? "Approved" : "ReconciliationRequired"
                : "Chargeback";
        }
        else if (paymentStatus == "in_mediation")
        {
            transaction.Status = "ReconciliationRequired";
            transaction.ReconciliationReason ??= ProviderInMediationReason;
        }
        else if (rejected && transaction.Status != "Approved")
        {
            transaction.Status = paymentStatus == "cancelled" ? "Cancelled" : "Rejected";
            transaction.CompletedAt ??= DateTime.UtcNow;
            transaction.ReconciliationReason = null;
        }

        if (transaction.Status != "ReconciliationRequired" &&
            !(manuallyConfirmedChargebackReimbursement && !hasRefundedAmount))
            transaction.ReconciliationReason = null;

        var reimbursementNeedsRefundAmount =
            paymentStatus == "charged_back" && statusDetail == "reimbursed" && !hasRefundedAmount &&
            !manuallyConfirmedChargebackReimbursement;
        if (reimbursementNeedsRefundAmount)
            transaction.ReconciliationReason ??= ReimbursementAmountMissingReason;

        if (sale != null)
        {
            if (requiresReconciliation)
                sale.FinancialStatus = "ReconciliationRequired";
            else if (rejected)
            {
                sale.RefundedAmount = 0m;
                sale.FinancialStatus = "Unpaid";
            }
            else if (isFullyRefunded)
                sale.RefundedAmount = sale.GrossAmount;
            if (!requiresReconciliation && !rejected && !isFullyRefunded && hasRefundedAmount)
                sale.RefundedAmount = Math.Clamp(refundedAmount, 0m, sale.GrossAmount);

            if (!requiresReconciliation && !rejected)
            {
                sale.FinancialStatus = reimbursementNeedsRefundAmount
                    ? "ReconciliationRequired"
                    : isFullyRefunded
                        ? paymentStatus == "refunded" ? "Refunded" : "Chargeback"
                        : isDisputed
                            ? statusDetail == "reimbursed" ? "Settled" : "Disputed"
                            : sale.RefundedAmount > 0 ? "PartiallyRefunded" : "Settled";
            }
        }

        await _db.SaveChangesAsync(cancellationToken);

        var approvedSessionNeedsRecovery = !requiresReconciliation &&
            approved && transaction.Status == "Approved" && previousTransactionStatus == "Approved" &&
            transaction.SendTelemetryToMachine &&
            await _db.MachineSessions.AsNoTracking().AnyAsync(session =>
                session.TransactionId == transaction.Id &&
                session.CompanyId == transaction.CompanyId &&
                session.State == MachineSessionStates.PaymentPending,
                cancellationToken);
        var shouldDispatchMachineResult = !requiresReconciliation &&
            (approvedSessionNeedsRecovery ||
             (approved && transaction.Status == "Approved" && previousTransactionStatus != "Approved") ||
             (rejected && transaction.Status is ("Rejected" or "Cancelled") &&
              previousTransactionStatus is not ("Rejected" or "Cancelled")));
        if (shouldDispatchMachineResult)
        {
            if (transaction.SendTelemetryToMachine)
            {
                await _sessionOrchestrator.HandlePaymentResultAsync(
                    transaction.Id,
                    approved,
                    approved ? "mercado_pago_webhook" : $"mercado_pago:{paymentStatus}",
                    cancellationToken);
            }
            else
            {
                await AddTransactionLogAsync(transaction.Id, "Info",
                    "Transação configurada para não enviar retorno MDB para a máquina.", cancellationToken);
            }
        }
        else
        {
            await AddTransactionLogAsync(transaction.Id, "Info",
                $"Webhook recebido; pagamento continua com status intermediário '{paymentStatus}'.", cancellationToken);
        }
    }

    private async Task<string> EnsureValidAccessTokenAsync(MercadoPagoIntegration integration, CancellationToken cancellationToken)
    {
        var accessToken = EncryptionService.Decrypt(integration.AccessToken);
        if (!integration.AccessTokenExpiresAt.HasValue || integration.AccessTokenExpiresAt > DateTime.UtcNow.AddMinutes(5))
        {
            return accessToken;
        }

        var refreshToken = EncryptionService.Decrypt(integration.RefreshToken);
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new InvalidOperationException("Token Mercado Pago expirado e sem refresh token disponível.");
        }

        var clientId = _configuration["MercadoPago:ClientId"] ?? integration.ClientId;
        var clientSecret = _configuration["MercadoPago:ClientSecret"] ?? EncryptionService.Decrypt(integration.ClientSecret);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.mercadopago.com/oauth/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_secret"] = clientSecret,
                ["client_id"] = clientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken
            })
        };

        using var response = await _httpClientFactory.CreateClient("MercadoPago").SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Renovação do token Mercado Pago falhou com HTTP {(int)response.StatusCode}.");
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var newAccessToken = root.GetProperty("access_token").GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(newAccessToken))
        {
            throw new InvalidOperationException("Mercado Pago retornou um token de acesso vazio.");
        }

        var newRefreshToken = root.TryGetProperty("refresh_token", out var refreshProperty)
            ? refreshProperty.GetString() ?? refreshToken
            : refreshToken;
        var expiresIn = root.TryGetProperty("expires_in", out var expiresProperty) && expiresProperty.TryGetInt32(out var seconds)
            ? seconds
            : 21600;
        integration.AccessToken = EncryptionService.Encrypt(newAccessToken);
        integration.RefreshToken = EncryptionService.Encrypt(newRefreshToken);
        integration.AccessTokenExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn);
        integration.UpdatedAt = DateTime.UtcNow;
        integration.LastTokenValidationAt = DateTime.UtcNow;
        integration.LastTokenValidationStatus = "Token OAuth renovado pelo worker de webhooks.";
        await _db.SaveChangesAsync(cancellationToken);
        return newAccessToken;
    }

    private async Task AddTransactionLogAsync(Guid transactionId, string type, string message, CancellationToken cancellationToken)
    {
        _db.TransactionTelemetryLogs.Add(new TransactionTelemetryLog
        {
            Id = Guid.NewGuid(),
            TransactionId = transactionId,
            Timestamp = DateTime.UtcNow,
            LogType = type,
            Message = PaymentLogRedactor.RedactMessage(message)
        });
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static bool TryReadDecimal(JsonElement element, string propertyName, out decimal result)
    {
        result = 0m;
        if (!element.TryGetProperty(propertyName, out var value))
            return false;
        if (value.ValueKind == JsonValueKind.Number)
            return value.TryGetDecimal(out result);
        return value.ValueKind == JsonValueKind.String &&
               decimal.TryParse(value.GetString(), System.Globalization.NumberStyles.Number,
                   System.Globalization.CultureInfo.InvariantCulture, out result);
    }
}
