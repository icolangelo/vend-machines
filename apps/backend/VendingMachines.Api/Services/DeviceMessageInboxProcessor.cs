using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Data;

namespace VendingMachines.Api.Services;

public interface IDeviceTelemetryMessageHandler
{
    Task HandleDeviceMessageAsync(InboundDeviceMessage message, CancellationToken cancellationToken);
}

public sealed class DeviceMessageInboxProcessor
{
    private const int MaximumAttempts = 8;
    private static readonly TimeSpan ProcessingLeaseDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ProcessingLeaseRenewalInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumMessageProcessingDuration = TimeSpan.FromSeconds(90);
    private static long _lastCleanupUtcTicks;
    private readonly AppDbContext _db;
    private readonly IDeviceTelemetryMessageHandler _handler;
    private readonly ILogger<DeviceMessageInboxProcessor> _logger;
    private readonly IServiceScopeFactory? _scopeFactory;

    public DeviceMessageInboxProcessor(
        AppDbContext db,
        IDeviceTelemetryMessageHandler handler,
        ILogger<DeviceMessageInboxProcessor> logger,
        IServiceScopeFactory? scopeFactory = null)
    {
        _db = db;
        _handler = handler;
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await CleanupProcessedRowsIfDueAsync(now, cancellationToken);
        var staleProcessingBefore = now.Subtract(ProcessingLeaseDuration);
        var candidate = await _db.DeviceMessageInbox.AsNoTracking()
            .Where(row =>
                ((row.Status == "Pending" || row.Status == "Retry") &&
                 (!row.NextAttemptAt.HasValue || row.NextAttemptAt <= now)) ||
                (row.Status == "Processing" && row.ProcessingStartedAt < staleProcessingBefore))
            .Where(row => !_db.DeviceMessageInbox.Any(previous =>
                previous.MachineId == row.MachineId &&
                previous.ReceivedAt < row.ReceivedAt &&
                (previous.Status == "Pending" || previous.Status == "Retry" || previous.Status == "Processing")))
            .OrderBy(row => row.ReceivedAt)
            .Select(row => new { row.Id, row.MachineId, row.Status, row.ProcessingStartedAt })
            .FirstOrDefaultAsync(cancellationToken);

        if (candidate is null) return false;

        var claimedAt = DateTime.UtcNow;
        int claimed;
        try
        {
            claimed = await _db.DeviceMessageInbox.Where(row => row.Id == candidate.Id &&
                    (((row.Status == "Pending" || row.Status == "Retry") &&
                      (!row.NextAttemptAt.HasValue || row.NextAttemptAt <= claimedAt)) ||
                     (row.Status == "Processing" && row.ProcessingStartedAt < claimedAt.Subtract(ProcessingLeaseDuration))))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.Status, "Processing")
                    .SetProperty(row => row.ProcessingStartedAt, claimedAt)
                    .SetProperty(row => row.Attempts, row => row.Attempts + 1), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ReleaseClaimAfterShutdownAsync(candidate.Id, claimedAt);
            throw;
        }

        if (claimed == 0) return true;

        long leaseTicks = claimedAt.Ticks;
        using var leaseRenewalStop = new CancellationTokenSource();
        using var handlerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var leaseRenewal = _scopeFactory is null
            ? Task.FromResult(true)
            : RenewProcessingLeaseAsync(candidate.Id, () => Interlocked.Read(ref leaseTicks),
                ticks => Interlocked.Exchange(ref leaseTicks, ticks), handlerCancellation, leaseRenewalStop.Token);

        try
        {
            var row = await _db.DeviceMessageInbox.AsNoTracking()
                .FirstAsync(candidateRow => candidateRow.Id == candidate.Id, cancellationToken);
            var message = new InboundDeviceMessage(
                row.MachineId,
                row.CompanyId,
                row.Serial,
                row.ConnectionId,
                row.MessageId,
                row.DataJson,
                row.ReceivedAt);

            await _handler.HandleDeviceMessageAsync(message, handlerCancellation.Token);
            var leaseIsOurs = await StopLeaseRenewalAsync(leaseRenewalStop, leaseRenewal);
            if (!leaseIsOurs) return true;
            var currentLeaseAt = new DateTime(Interlocked.Read(ref leaseTicks), DateTimeKind.Utc);
            var completed = await _db.DeviceMessageInbox.Where(inbox => inbox.Id == row.Id &&
                    inbox.Status == "Processing" && inbox.ProcessingStartedAt == currentLeaseAt)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(inbox => inbox.Status, "Processed")
                    .SetProperty(inbox => inbox.ProcessedAt, DateTime.UtcNow)
                    .SetProperty(inbox => inbox.ProcessingStartedAt, (DateTime?)null)
                    .SetProperty(inbox => inbox.NextAttemptAt, (DateTime?)null)
                    .SetProperty(inbox => inbox.LastError, (string?)null), cancellationToken);
            if (completed == 0)
                _logger.LogWarning("Worker MDB perdeu a posse do lease da mensagem {InboxId}; o resultado desta tentativa foi descartado.", row.Id);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await StopLeaseRenewalAsync(leaseRenewalStop, leaseRenewal);
            await ReleaseClaimAfterShutdownAsync(candidate.Id, new DateTime(Interlocked.Read(ref leaseTicks), DateTimeKind.Utc));
            throw;
        }
        catch (Exception exception)
        {
            await StopLeaseRenewalAsync(leaseRenewalStop, leaseRenewal);
            var currentLeaseAt = new DateTime(Interlocked.Read(ref leaseTicks), DateTimeKind.Utc);
            var attempts = await _db.DeviceMessageInbox
                .Where(inbox => inbox.Id == candidate.Id &&
                    inbox.Status == "Processing" && inbox.ProcessingStartedAt == currentLeaseAt)
                .Select(inbox => (int?)inbox.Attempts)
                .SingleOrDefaultAsync(cancellationToken);
            if (!attempts.HasValue)
            {
                _logger.LogWarning(exception,
                    "Worker MDB perdeu o lease da mensagem {InboxId} da máquina {MachineId}; a falha desta tentativa não alterou o estado atual.",
                    candidate.Id, candidate.MachineId);
                return true;
            }

            var terminal = attempts.Value >= MaximumAttempts;
            var delaySeconds = Math.Min(60, 5 * Math.Pow(2, Math.Min(attempts.Value - 1, 4)));
            var safeError = exception.GetType().Name;
            var updated = await _db.DeviceMessageInbox.Where(inbox => inbox.Id == candidate.Id &&
                    inbox.Status == "Processing" && inbox.ProcessingStartedAt == currentLeaseAt)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(inbox => inbox.Status, terminal ? "Failed" : "Retry")
                    .SetProperty(inbox => inbox.ProcessingStartedAt, (DateTime?)null)
                    .SetProperty(inbox => inbox.NextAttemptAt, terminal ? (DateTime?)null : DateTime.UtcNow.AddSeconds(delaySeconds))
                    .SetProperty(inbox => inbox.LastError, safeError), cancellationToken);

            if (updated == 0)
            {
                _logger.LogWarning(exception,
                    "Worker MDB perdeu o lease da mensagem {InboxId} da máquina {MachineId}; a falha desta tentativa não alterou o estado atual.",
                    candidate.Id, candidate.MachineId);
            }
            else
            {
                _logger.LogError(exception,
                    "Falha ao processar evento MDB {InboxId} da máquina {MachineId}; tentativa {Attempt}/{MaxAttempts}; estado {Status}.",
                    candidate.Id, candidate.MachineId, attempts.Value, MaximumAttempts, terminal ? "Failed" : "Retry");
            }
        }

        return true;
    }

    private async Task<bool> RenewProcessingLeaseAsync(
        Guid inboxId,
        Func<long> getLeaseTicks,
        Action<long> setLeaseTicks,
        CancellationTokenSource handlerCancellation,
        CancellationToken renewalStopToken)
    {
        using var timer = new PeriodicTimer(ProcessingLeaseRenewalInterval);
        var processingDuration = Stopwatch.StartNew();
        try
        {
            while (await timer.WaitForNextTickAsync(renewalStopToken))
            {
                if (processingDuration.Elapsed >= MaximumMessageProcessingDuration)
                {
                    _logger.LogWarning("O processamento da mensagem MDB {InboxId} excedeu o limite de 90 segundos; a execução será retomada por outra tentativa.", inboxId);
                    handlerCancellation.Cancel();
                    return false;
                }

                var previousLeaseAt = new DateTime(getLeaseTicks(), DateTimeKind.Utc);
                var renewedAt = DateTime.UtcNow;
                if (renewedAt <= previousLeaseAt)
                    renewedAt = previousLeaseAt.AddTicks(10);

                using var scope = _scopeFactory!.CreateScope();
                var leaseDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                using var renewalTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var renewed = await leaseDb.DeviceMessageInbox
                    .Where(inbox => inbox.Id == inboxId && inbox.Status == "Processing" &&
                                    inbox.ProcessingStartedAt == previousLeaseAt)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(inbox => inbox.ProcessingStartedAt, renewedAt), renewalTimeout.Token);
                if (renewed != 1)
                {
                    _logger.LogWarning("Worker MDB perdeu o lease da mensagem {InboxId}; a execução em andamento será cancelada.", inboxId);
                    handlerCancellation.Cancel();
                    return false;
                }

                setLeaseTicks(renewedAt.Ticks);
            }

            return true;
        }
        catch (OperationCanceledException) when (renewalStopToken.IsCancellationRequested)
        {
            return true;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Não foi possível renovar o lease da mensagem MDB {InboxId}; a execução em andamento será cancelada. Tipo de erro: {ExceptionType}.",
                inboxId,
                exception.GetType().Name);
            handlerCancellation.Cancel();
            return false;
        }
    }

    private static async Task<bool> StopLeaseRenewalAsync(CancellationTokenSource stop, Task<bool> renewal)
    {
        stop.Cancel();
        try
        {
            return await renewal;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

    private async Task ReleaseClaimAfterShutdownAsync(Guid inboxId, DateTime claimedAt)
    {
        try
        {
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var retryAt = DateTime.UtcNow;
            var released = await _db.DeviceMessageInbox
                .Where(inbox => inbox.Id == inboxId &&
                    inbox.Status == "Processing" && inbox.ProcessingStartedAt == claimedAt)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(inbox => inbox.Status, "Retry")
                    .SetProperty(inbox => inbox.ProcessingStartedAt, (DateTime?)null)
                    .SetProperty(inbox => inbox.NextAttemptAt, (DateTime?)retryAt)
                    .SetProperty(inbox => inbox.Attempts, inbox => inbox.Attempts > 0 ? inbox.Attempts - 1 : 0)
                    .SetProperty(inbox => inbox.LastError, (string?)null), cleanupTimeout.Token);

            if (released > 0)
                _logger.LogInformation("Mensagem MDB {InboxId} liberada para retomada imediata após cancelamento do worker.", inboxId);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Não foi possível liberar o lease da mensagem MDB {InboxId} durante o encerramento; ela será retomada após expiração do lease. Tipo de erro: {ExceptionType}.",
                inboxId,
                exception.GetType().Name);
        }
    }

    public Task<int> CountUnfinishedAsync(CancellationToken cancellationToken) =>
        _db.DeviceMessageInbox.CountAsync(
            row => row.Status == "Pending" || row.Status == "Retry" || row.Status == "Processing",
            cancellationToken);

    private async Task CleanupProcessedRowsIfDueAsync(DateTime now, CancellationToken cancellationToken)
    {
        var nowTicks = now.Ticks;
        var lastCleanup = Volatile.Read(ref _lastCleanupUtcTicks);
        if (nowTicks - lastCleanup < TimeSpan.FromHours(12).Ticks ||
            Interlocked.CompareExchange(ref _lastCleanupUtcTicks, nowTicks, lastCleanup) != lastCleanup)
            return;

        await _db.DeviceMessageInbox
            .Where(row => row.Status == "Processed" && row.ProcessedAt < now.AddDays(-90))
            .ExecuteDeleteAsync(cancellationToken);
    }
}
