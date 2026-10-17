using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;

namespace VendingMachines.Api.Services;

public sealed class MercadoPagoWebhookProcessor
{
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
        var candidate = await _db.WebhookInboxMessages
            .AsNoTracking()
            .Where(message =>
                ((message.Status == "Pending" || message.Status == "Retry") &&
                 (!message.NextAttemptAt.HasValue || message.NextAttemptAt <= now)) ||
                (message.Status == "Processing" && message.ProcessingStartedAt < now.AddMinutes(-2)))
            .OrderBy(message => message.ReceivedAt)
            .Select(message => new { message.Id, message.Status, message.ProcessingStartedAt })
            .FirstOrDefaultAsync(cancellationToken);

        if (candidate is null)
        {
            return false;
        }

        var staleProcessingCutoff = now.AddMinutes(-2);
        var claim = _db.WebhookInboxMessages.Where(message => message.Id == candidate.Id &&
            ((message.Status == "Pending" || message.Status == "Retry") &&
             (!message.NextAttemptAt.HasValue || message.NextAttemptAt <= now) ||
             (message.Status == "Processing" && message.ProcessingStartedAt < staleProcessingCutoff)))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(message => message.Status, "Processing")
                .SetProperty(message => message.ProcessingStartedAt, now)
                .SetProperty(message => message.Attempts, message => message.Attempts + 1), cancellationToken);

        if (await claim == 0)
        {
            return true;
        }

        try
        {
            await ProcessPaymentAsync(candidate.Id, cancellationToken);
            await _db.WebhookInboxMessages.Where(message => message.Id == candidate.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(message => message.Status, "Processed")
                    .SetProperty(message => message.ProcessedAt, DateTime.UtcNow)
                    .SetProperty(message => message.ProcessingStartedAt, (DateTime?)null)
                    .SetProperty(message => message.NextAttemptAt, (DateTime?)null)
                    .SetProperty(message => message.LastError, (string?)null), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var attempts = await _db.WebhookInboxMessages.Where(message => message.Id == candidate.Id)
                .Select(message => message.Attempts).SingleAsync(cancellationToken);
            var isFinal = attempts >= MaxAttempts;
            var delaySeconds = Math.Min(3600, 5 * Math.Pow(2, Math.Min(attempts - 1, 9)));
            var safeError = exception.GetType().Name;
            await _db.WebhookInboxMessages.Where(message => message.Id == candidate.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(message => message.Status, isFinal ? "Failed" : "Retry")
                    .SetProperty(message => message.ProcessingStartedAt, (DateTime?)null)
                    .SetProperty(message => message.NextAttemptAt, isFinal ? (DateTime?)null : DateTime.UtcNow.AddSeconds(delaySeconds))
                    .SetProperty(message => message.LastError, safeError), cancellationToken);

            _logger.LogError(exception,
                "Falha no processamento do webhook {WebhookInboxId}; tentativa {Attempt}/{MaxAttempts}; estado {Status}.",
                candidate.Id, attempts, MaxAttempts, isFinal ? "Failed" : "Retry");
        }

        return true;
    }

    public async Task ReconcilePendingPaymentsAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var eligibleBefore = now.AddMinutes(-2);
        var lastReconciledBefore = now.AddMinutes(-5);
        var terminalReconciledBefore = now.AddHours(-1);
        var rejectedReconciledBefore = now.AddHours(-24);
        var transactionIds = await _db.PaymentTransactions
            .AsNoTracking()
            .Where(transaction =>
                transaction.MercadoPagoPaymentId != null &&
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
        }
    }

    private async Task ProcessPaymentAsync(Guid inboxId, CancellationToken cancellationToken)
    {
        var inbox = await _db.WebhookInboxMessages.AsNoTracking()
            .SingleAsync(message => message.Id == inboxId, cancellationToken);
        await ProcessPaymentByIdAsync(inbox.PaymentId, cancellationToken);
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
        if (approved)
        {
            transaction.Status = "Approved";
            transaction.CompletedAt ??= DateTime.UtcNow;
        }
        else if (paymentStatus == "refunded")
        {
            transaction.Status = "Refunded";
            transaction.CompletedAt ??= DateTime.UtcNow;
        }
        else if (paymentStatus == "charged_back")
        {
            transaction.Status = statusDetail == "reimbursed"
                ? hasRefundedAmount ? "Approved" : "ReconciliationRequired"
                : "Chargeback";
        }
        else if (paymentStatus == "in_mediation")
        {
            transaction.Status = "ReconciliationRequired";
        }
        else if (rejected && transaction.Status != "Approved")
        {
            transaction.Status = "Rejected";
            transaction.CompletedAt ??= DateTime.UtcNow;
        }

        var sale = await _db.SaleRecords.FirstOrDefaultAsync(
            record => record.TransactionId == transaction.Id, cancellationToken);
        if (sale != null)
        {
            var reimbursementNeedsRefundAmount =
                paymentStatus == "charged_back" && statusDetail == "reimbursed" && !hasRefundedAmount;
            if (isFullyRefunded)
                sale.RefundedAmount = sale.GrossAmount;
            else if (hasRefundedAmount)
                sale.RefundedAmount = Math.Clamp(refundedAmount, 0m, sale.GrossAmount);

            sale.FinancialStatus = reimbursementNeedsRefundAmount
                ? "ReconciliationRequired"
                : isFullyRefunded
                    ? paymentStatus == "refunded" ? "Refunded" : "Chargeback"
                    : isDisputed
                        ? statusDetail == "reimbursed" ? "Settled" : "Disputed"
                        : sale.RefundedAmount > 0 ? "PartiallyRefunded" : "Settled";
        }

        await _db.SaveChangesAsync(cancellationToken);

        if ((approved && previousTransactionStatus != "Approved") ||
            (rejected && previousTransactionStatus != "Rejected"))
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
            Message = message
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
