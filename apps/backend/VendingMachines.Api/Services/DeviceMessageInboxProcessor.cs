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
    private static long _lastCleanupUtcTicks;
    private readonly AppDbContext _db;
    private readonly IDeviceTelemetryMessageHandler _handler;
    private readonly ILogger<DeviceMessageInboxProcessor> _logger;

    public DeviceMessageInboxProcessor(
        AppDbContext db,
        IDeviceTelemetryMessageHandler handler,
        ILogger<DeviceMessageInboxProcessor> logger)
    {
        _db = db;
        _handler = handler;
        _logger = logger;
    }

    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await CleanupProcessedRowsIfDueAsync(now, cancellationToken);
        var staleProcessingBefore = now.AddMinutes(-2);
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
            .Select(row => new { row.Id, row.Status, row.ProcessingStartedAt })
            .FirstOrDefaultAsync(cancellationToken);

        if (candidate is null) return false;

        var claimedAt = DateTime.UtcNow;
        var claimed = await _db.DeviceMessageInbox.Where(row => row.Id == candidate.Id &&
                (((row.Status == "Pending" || row.Status == "Retry") &&
                  (!row.NextAttemptAt.HasValue || row.NextAttemptAt <= claimedAt)) ||
                 (row.Status == "Processing" && row.ProcessingStartedAt < claimedAt.AddMinutes(-2))))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Status, "Processing")
                .SetProperty(row => row.ProcessingStartedAt, claimedAt)
                .SetProperty(row => row.Attempts, row => row.Attempts + 1), cancellationToken);

        if (claimed == 0) return true;

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

        try
        {
            await _handler.HandleDeviceMessageAsync(message, cancellationToken);
            await _db.DeviceMessageInbox.Where(inbox => inbox.Id == row.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(inbox => inbox.Status, "Processed")
                    .SetProperty(inbox => inbox.ProcessedAt, DateTime.UtcNow)
                    .SetProperty(inbox => inbox.ProcessingStartedAt, (DateTime?)null)
                    .SetProperty(inbox => inbox.NextAttemptAt, (DateTime?)null)
                    .SetProperty(inbox => inbox.LastError, (string?)null), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var attempts = row.Attempts;
            var terminal = attempts >= MaximumAttempts;
            var delaySeconds = Math.Min(60, 5 * Math.Pow(2, Math.Min(attempts - 1, 4)));
            var safeError = exception.GetType().Name;
            await _db.DeviceMessageInbox.Where(inbox => inbox.Id == row.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(inbox => inbox.Status, terminal ? "Failed" : "Retry")
                    .SetProperty(inbox => inbox.ProcessingStartedAt, (DateTime?)null)
                    .SetProperty(inbox => inbox.NextAttemptAt, terminal ? (DateTime?)null : DateTime.UtcNow.AddSeconds(delaySeconds))
                    .SetProperty(inbox => inbox.LastError, safeError), cancellationToken);

            _logger.LogError(exception,
                "Falha ao processar evento MDB {InboxId} da máquina {MachineId}; tentativa {Attempt}/{MaxAttempts}; estado {Status}.",
                row.Id, row.MachineId, attempts, MaximumAttempts, terminal ? "Failed" : "Retry");
        }

        return true;
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
