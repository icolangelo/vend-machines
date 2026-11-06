using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;

namespace VendingMachines.Api.Services;

public sealed class WebhookInboxAdmissionService
{
    private const int DefaultMaximumOutstandingMessages = 5000;
    private const int AdmissionLockNamespace = 22094;
    private const int AdmissionLockId = 18766;
    private readonly AppDbContext _db;
    private readonly WebhookInboxAdmissionGate _admissionGate;
    private readonly int _maximumOutstandingMessages;

    public WebhookInboxAdmissionService(
        AppDbContext db,
        IConfiguration? configuration,
        WebhookInboxAdmissionGate admissionGate)
    {
        _db = db;
        _admissionGate = admissionGate;
        _maximumOutstandingMessages = Math.Clamp(
            configuration?.GetValue<int?>("Payments:MaxOutstandingWebhookMessages") ?? DefaultMaximumOutstandingMessages,
            100,
            100_000);
    }

    public async Task<bool> PersistAsync(WebhookInboxMessage message, CancellationToken cancellationToken)
    {
        await _admissionGate.Semaphore.WaitAsync(cancellationToken);
        try
        {
            await using var transaction = await BeginAdmissionTransactionAsync(cancellationToken);
            if (await _db.WebhookInboxMessages.AnyAsync(row => row.EventKey == message.EventKey, cancellationToken))
            {
                await transaction.CommitAsync(cancellationToken);
                return true;
            }

            var outstandingCount = await _db.WebhookInboxMessages.CountAsync(
                row => row.Status != "Processed",
                cancellationToken);
            if (outstandingCount >= _maximumOutstandingMessages)
                throw new WebhookInboxFullException(_maximumOutstandingMessages);

            _db.WebhookInboxMessages.Add(message);
            try
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                await transaction.RollbackAsync(cancellationToken);
                _db.ChangeTracker.Clear();
                if (await _db.WebhookInboxMessages.AnyAsync(row => row.EventKey == message.EventKey, cancellationToken))
                    return true;
                throw;
            }

            await transaction.CommitAsync(cancellationToken);
            return false;
        }
        finally
        {
            _admissionGate.Semaphore.Release();
        }
    }

    public async Task<int> RequeueFailedAsync(Guid id, CancellationToken cancellationToken)
    {
        await _admissionGate.Semaphore.WaitAsync(cancellationToken);
        try
        {
            await using var transaction = await BeginAdmissionTransactionAsync(cancellationToken);
            var failedMessageExists = await _db.WebhookInboxMessages.AnyAsync(
                row => row.Id == id && row.Status == "Failed", cancellationToken);
            if (!failedMessageExists)
                return 0;

            var now = DateTime.UtcNow;
            var updated = await _db.WebhookInboxMessages
                .Where(row => row.Id == id && row.Status == "Failed")
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.Status, "Pending")
                    .SetProperty(row => row.Attempts, 0)
                    .SetProperty(row => row.NextAttemptAt, (DateTime?)now)
                    .SetProperty(row => row.ProcessingStartedAt, (DateTime?)null)
                    .SetProperty(row => row.ProcessedAt, (DateTime?)null)
                    .SetProperty(row => row.LastError, (string?)null), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return updated;
        }
        finally
        {
            _admissionGate.Semaphore.Release();
        }
    }

    private async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction> BeginAdmissionTransactionAsync(
        CancellationToken cancellationToken)
    {
        var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            if (_db.Database.IsNpgsql())
            {
                await _db.Database.ExecuteSqlRawAsync(
                    $"SELECT pg_advisory_xact_lock({AdmissionLockNamespace}, {AdmissionLockId})",
                    cancellationToken);
            }

            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }
}

public sealed class WebhookInboxAdmissionGate
{
    public static WebhookInboxAdmissionGate Shared { get; } = new();
    public SemaphoreSlim Semaphore { get; } = new(1, 1);
}

public sealed class WebhookInboxFullException(int limit)
    : InvalidOperationException($"A fila durável de webhooks atingiu o limite de {limit} mensagens não processadas.");
