using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VendingMachines.Api.Services;
using VendingMachines.Api.Data;

namespace VendingMachines.Api.Controllers;

[Authorize(Roles = AppRoles.PlatformAdmin)]
[ApiController]
[Route("api/admin/worker-health")]
public sealed class WorkerHealthController(
    BackgroundWorkerHealthRegistry health,
    AppDbContext db,
    IConfiguration configuration,
    ILogger<WorkerHealthController>? logger = null,
    LegacyTelemetryRequestRelay? legacyTelemetryRelay = null) : ControllerBase
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
            var financialOutboxCounts = await db.OutboxMessages.AsNoTracking()
                .GroupBy(row => row.Status)
                .Select(group => new { status = group.Key, count = group.Count() })
                .ToDictionaryAsync(item => item.status, item => item.count, cancellationToken);
            var oldestDeviceMessageAt = await db.DeviceMessageInbox.AsNoTracking()
                .Where(row => row.Status == "Pending" || row.Status == "Retry" || row.Status == "Processing")
                .MinAsync(row => (DateTime?)row.ReceivedAt, cancellationToken);
            var oldestWebhookAt = await db.WebhookInboxMessages.AsNoTracking()
                .Where(row => row.Status == "Pending" || row.Status == "Retry" || row.Status == "Processing")
                .MinAsync(row => (DateTime?)row.ReceivedAt, cancellationToken);
            var oldestPendingFinancialOutboxAt = await db.OutboxMessages.AsNoTracking()
                .Where(row => row.Status == "pending")
                .MinAsync(row => (DateTime?)row.CreatedAt, cancellationToken);
            var oldestDeferredLegacyFinancialOutboxAt = await db.OutboxMessages.AsNoTracking()
                .Where(row => row.Status == "deferred_legacy_overlap")
                .MinAsync(row => (DateTime?)row.CreatedAt, cancellationToken);
            var earliestProcessingFinancialOutboxLeaseExpiryAt = await db.OutboxMessages.AsNoTracking()
                .Where(row => row.Status == "processing")
                .MinAsync(row => row.NextAttemptAt, cancellationToken);
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
            var unresolvedLocalCommands = await db.TelemetryCommands.AsNoTracking()
                .Where(row => (row.Status == "accepted" && row.Error == "command_intent_persisted") ||
                              (row.Status == "failed" &&
                               (row.Error == "ack_timeout" || row.Error == "command_outcome_unknown") &&
                               !db.TelemetryCommandResolutions.Any(resolution => resolution.TelemetryCommandId == row.Id)))
                .Select(row => new { row.Status, row.CreatedAt })
                .ToListAsync(cancellationToken);
            var oldestLocalIntentAt = unresolvedLocalCommands
                .Where(row => row.Status == "accepted")
                .Select(row => (DateTime?)row.CreatedAt)
                .Min();
            var oldestLocalUnresolvedResultAt = unresolvedLocalCommands
                .Where(row => row.Status == "failed")
                .Select(row => (DateTime?)row.CreatedAt)
                .Min();
            var pendingPayments = db.PaymentTransactions.AsNoTracking()
                .Where(payment => payment.Status == "Pending" || payment.Status == "ReconciliationRequired");
            var pendingPaymentCount = await pendingPayments.CountAsync(cancellationToken);
            var oldestPendingPaymentAt = await pendingPayments
                .MinAsync(payment => (DateTime?)payment.CreatedAt, cancellationToken);
            var freshnessCutoff = DateTime.UtcNow - MachineConnectionFreshness.Window;
            var monitoredMachines = db.MachineConnectionStates.AsNoTracking()
                .Where(state => state.MonitoringEnabled);
            var onlineMachines = monitoredMachines.Where(state => state.IsOnline);
            var onlineMachineCount = await onlineMachines.CountAsync(cancellationToken);
            var staleOnlineMachineCount = await onlineMachines
                .CountAsync(state => state.LastSeenAt == null || state.LastSeenAt < freshnessCutoff, cancellationToken);
            var onlineWithoutOwnerCount = await onlineMachines
                .CountAsync(state => state.OwnerNodeId == null, cancellationToken);
            var onlineWithoutCommandRoutingCount = await onlineMachines
                .CountAsync(state => state.CommandRoutingVersion != TelemetryConnectionManager.CurrentCommandRoutingVersion, cancellationToken);
            var recentOnlineConnections = db.MachineConnectionStates.AsNoTracking()
                .Where(state => state.IsOnline &&
                                (state.LastSeenAt == null || state.LastSeenAt >= freshnessCutoff));
            var unroutableRecentOnlineConnections = MachineConnectionReadiness
                .GetUnroutableRecentOnlineConnections(db, DateTime.UtcNow);
            var legacyRelayEligibleConnections = MachineConnectionReadiness
                .GetLegacyRelayEligibleConnections(db, DateTime.UtcNow);
            var legacyRelayEligibleCount = await legacyRelayEligibleConnections.CountAsync(cancellationToken);
            var legacyRelayConfigured = legacyTelemetryRelay?.IsConfigured == true;
            var legacyRelayHealthy = legacyRelayEligibleCount > 0 && legacyRelayConfigured &&
                                     await legacyTelemetryRelay!.IsLegacyApiReadyAsync(cancellationToken);
            var releaseBlockingConnections = legacyRelayHealthy
                ? unroutableRecentOnlineConnections.Where(connection =>
                    !legacyRelayEligibleConnections.Any(legacy => legacy.MachineId == connection.MachineId))
                : unroutableRecentOnlineConnections;
            var readinessRecentOnlineCount = await recentOnlineConnections.CountAsync(cancellationToken);
            var readinessUnroutableCount = await releaseBlockingConnections.CountAsync(cancellationToken);
            var readinessMissingOwnerCount = await releaseBlockingConnections.CountAsync(
                state => state.ConnectionId == null || state.ConnectionId == "" ||
                         state.OwnerNodeId == null || state.OwnerNodeId == "" ||
                         state.OwnerConnectionId != state.ConnectionId,
                cancellationToken);
            var readinessUnsupportedRoutingCount = await releaseBlockingConnections.CountAsync(
                state => state.CommandRoutingVersion != TelemetryConnectionManager.CurrentCommandRoutingVersion,
                cancellationToken);
            var readinessInvalidLeaseCount = await releaseBlockingConnections.CountAsync(
                state => state.OwnerLeaseExpiresAt == null || state.OwnerLeaseExpiresAt <= DateTime.UtcNow,
                cancellationToken);
            var readinessCompanyMismatchCount = await releaseBlockingConnections.CountAsync(
                state => !db.Machines.Any(machine => machine.Id == state.MachineId &&
                                                    machine.CompanyId == state.CompanyId),
                cancellationToken);

            queueMetrics = new
            {
                available = true,
                machineConnections = new
                {
                    monitored = await monitoredMachines.CountAsync(cancellationToken),
                    online = onlineMachineCount,
                    staleOnline = staleOnlineMachineCount,
                    onlineWithoutOwner = onlineWithoutOwnerCount,
                    onlineWithoutCommandRouting = onlineWithoutCommandRoutingCount,
                    legacyRelay = new
                    {
                        eligibleConnections = legacyRelayEligibleCount,
                        configured = legacyRelayConfigured,
                        healthy = legacyRelayEligibleCount == 0 ? (bool?)null : legacyRelayHealthy
                    },
                    readinessGate = new
                    {
                        onlineInGateScope = readinessRecentOnlineCount,
                        unroutable = readinessUnroutableCount,
                        withoutOwner = readinessMissingOwnerCount,
                        withoutCommandRouting = readinessUnsupportedRoutingCount,
                        withoutValidLease = readinessInvalidLeaseCount,
                        companyMismatch = readinessCompanyMismatchCount
                    }
                },
                deviceMessageInbox = deviceInboxCounts,
                oldestDeviceMessageAgeSeconds = GetAgeSeconds(oldestDeviceMessageAt),
                telemetryCommandDispatches = telemetryDispatchCounts,
                oldestPendingTelemetryDispatchAgeSeconds = GetAgeSeconds(oldestPendingTelemetryDispatchAt),
                oldestProcessingTelemetryDispatchAgeSeconds = GetAgeSeconds(oldestProcessingTelemetryDispatchAt),
                localTelemetryCommands = new
                {
                    pendingIntents = unresolvedLocalCommands.Count(row => row.Status == "accepted"),
                    oldestPendingIntentAgeSeconds = GetAgeSeconds(oldestLocalIntentAt),
                    unresolvedResults = unresolvedLocalCommands.Count(row => row.Status == "failed"),
                    oldestUnresolvedResultAgeSeconds = GetAgeSeconds(oldestLocalUnresolvedResultAt)
                },
                webhookInbox = webhookInboxCounts,
                oldestWebhookAgeSeconds = GetAgeSeconds(oldestWebhookAt),
                financialOutbox = financialOutboxCounts,
                oldestPendingFinancialOutboxAgeSeconds = GetAgeSeconds(oldestPendingFinancialOutboxAt),
                oldestDeferredLegacyFinancialOutboxAgeSeconds = GetAgeSeconds(oldestDeferredLegacyFinancialOutboxAt),
                earliestProcessingFinancialOutboxLeaseExpiryAt,
                pendingPayments = pendingPaymentCount,
                oldestPendingPaymentAgeSeconds = GetAgeSeconds(oldestPendingPaymentAt)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger?.LogWarning(
                "Não foi possível carregar as métricas agregadas de saúde operacional ({ExceptionType}).",
                exception.GetType().Name);
            queueMetrics = new { available = false };
        }

        return Ok(new
        {
            generatedAt = DateTime.UtcNow,
            sourceCommit = ReleaseMetadata.GetSourceCommit(configuration["APP_REVISION"]),
            workers,
            hasUnhealthyWorkers = workers.Any(worker => !BackgroundWorkerStatus.IsHealthy(worker.Status)),
            queueMetrics
        });
    }

    private static long? GetAgeSeconds(DateTime? oldestAt) => oldestAt is null
        ? null
        : Math.Max(0, (long)(DateTime.UtcNow - oldestAt.Value).TotalSeconds);
}
