using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Services;
using VendingMachines.Api.Data;

namespace VendingMachines.Api.Controllers;

[Authorize(Roles = AppRoles.PlatformAdmin)]
[ApiController]
[Route("api/admin/worker-health")]
public sealed class WorkerHealthController(BackgroundWorkerHealthRegistry health, AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var workers = health.GetSnapshots();
        object queueMetrics;
        try
        {
            var deviceInboxCounts = await db.DeviceMessageInbox.AsNoTracking()
                .GroupBy(row => row.Status)
                .Select(group => new { status = group.Key, count = group.Count() })
                .ToDictionaryAsync(item => item.status, item => item.count, cancellationToken);
            var webhookInboxCounts = await db.WebhookInboxMessages.AsNoTracking()
                .GroupBy(row => row.Status)
                .Select(group => new { status = group.Key, count = group.Count() })
                .ToDictionaryAsync(item => item.status, item => item.count, cancellationToken);
            var oldestDeviceMessageAt = await db.DeviceMessageInbox.AsNoTracking()
                .Where(row => row.Status == "Pending" || row.Status == "Retry" || row.Status == "Processing")
                .MinAsync(row => (DateTime?)row.ReceivedAt, cancellationToken);
            var oldestWebhookAt = await db.WebhookInboxMessages.AsNoTracking()
                .Where(row => row.Status == "Pending" || row.Status == "Retry" || row.Status == "Processing")
                .MinAsync(row => (DateTime?)row.ReceivedAt, cancellationToken);
            var telemetryDispatchCounts = await db.TelemetryCommandDispatches.AsNoTracking()
                .GroupBy(row => row.Status)
                .Select(group => new { status = group.Key, count = group.Count() })
                .ToDictionaryAsync(item => item.status, item => item.count, cancellationToken);
            var oldestPendingTelemetryDispatchAt = await db.TelemetryCommandDispatches.AsNoTracking()
                .Where(row => row.Status == "Pending")
                .MinAsync(row => (DateTime?)row.CreatedAt, cancellationToken);
            var oldestProcessingTelemetryDispatchAt = await db.TelemetryCommandDispatches.AsNoTracking()
                .Where(row => row.Status == "Processing")
                .MinAsync(row => (DateTime?)row.ProcessingStartedAt, cancellationToken);
            var pendingPayments = db.PaymentTransactions.AsNoTracking()
                .Where(payment => payment.Status == "Pending" || payment.Status == "ReconciliationRequired");
            var pendingPaymentCount = await pendingPayments.CountAsync(cancellationToken);
            var oldestPendingPaymentAt = await pendingPayments
                .MinAsync(payment => (DateTime?)payment.CreatedAt, cancellationToken);
            var freshnessCutoff = DateTime.UtcNow.AddSeconds(-90);
            var monitoredMachines = db.MachineConnectionStates.AsNoTracking()
                .Where(state => state.MonitoringEnabled);
            var onlineMachines = monitoredMachines.Where(state => state.IsOnline);
            var onlineMachineCount = await onlineMachines.CountAsync(cancellationToken);
            var staleOnlineMachineCount = await onlineMachines
                .CountAsync(state => state.LastSeenAt == null || state.LastSeenAt < freshnessCutoff, cancellationToken);
            var onlineWithoutOwnerCount = await onlineMachines
                .CountAsync(state => state.OwnerNodeId == null, cancellationToken);

            queueMetrics = new
            {
                available = true,
                machineConnections = new
                {
                    monitored = await monitoredMachines.CountAsync(cancellationToken),
                    online = onlineMachineCount,
                    staleOnline = staleOnlineMachineCount,
                    onlineWithoutOwner = onlineWithoutOwnerCount
                },
                deviceMessageInbox = deviceInboxCounts,
                oldestDeviceMessageAgeSeconds = GetAgeSeconds(oldestDeviceMessageAt),
                telemetryCommandDispatches = telemetryDispatchCounts,
                oldestPendingTelemetryDispatchAgeSeconds = GetAgeSeconds(oldestPendingTelemetryDispatchAt),
                oldestProcessingTelemetryDispatchAgeSeconds = GetAgeSeconds(oldestProcessingTelemetryDispatchAt),
                webhookInbox = webhookInboxCounts,
                oldestWebhookAgeSeconds = GetAgeSeconds(oldestWebhookAt),
                pendingPayments = pendingPaymentCount,
                oldestPendingPaymentAgeSeconds = GetAgeSeconds(oldestPendingPaymentAt)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            queueMetrics = new { available = false };
        }

        return Ok(new
        {
            generatedAt = DateTime.UtcNow,
            workers,
            hasUnhealthyWorkers = workers.Any(worker => worker.Status is "NotStarted" or "Degraded" or "Stale" or "Stopped"),
            queueMetrics
        });
    }

    private static long? GetAgeSeconds(DateTime? oldestAt) => oldestAt is null
        ? null
        : Math.Max(0, (long)(DateTime.UtcNow - oldestAt.Value).TotalSeconds);
}
