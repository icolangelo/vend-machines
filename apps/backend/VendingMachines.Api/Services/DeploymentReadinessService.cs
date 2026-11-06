using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;

namespace VendingMachines.Api.Services;

public sealed class DeploymentReadinessService(
    AppDbContext db,
    LegacyTelemetryRequestRelay? legacyTelemetryRelay = null)
{
    public async Task<string?> GetBlockerAsync(CancellationToken cancellationToken)
    {
        var admissionGate = await db.SessionAdmissionGates.AsNoTracking()
            .SingleOrDefaultAsync(gate => gate.Id == SessionAdmissionGate.SingletonId, cancellationToken);
        if (admissionGate is null)
            return "session_admission_gate_unavailable";

        if (await PlatformFeeSettingsReader.ReadValidFeePercentAsync(db, cancellationToken) is null)
            return "invalid_platform_fee_settings";

        var utcNow = DateTime.UtcNow;
        var unroutableConnections = MachineConnectionReadiness.GetUnroutableRecentOnlineConnections(db, utcNow);
        var legacyRelayEligibleConnections = MachineConnectionReadiness.GetLegacyRelayEligibleConnections(db, utcNow);
        if (await unroutableConnections.AnyAsync(
                connection => !legacyRelayEligibleConnections.Any(legacy => legacy.MachineId == connection.MachineId),
                cancellationToken))
            return "machine_connections_unroutable";

        if (await legacyRelayEligibleConnections.AnyAsync(cancellationToken) &&
            (legacyTelemetryRelay?.IsConfigured != true ||
             !await legacyTelemetryRelay.IsLegacyApiReadyAsync(cancellationToken)))
            return "legacy_owner_api_unavailable";

        var sessionsWithoutRoutableConnection = MachineConnectionReadiness.GetOpenSessionsWithoutRoutableConnection(db, utcNow);
        if (await sessionsWithoutRoutableConnection.AnyAsync(
                session => !legacyRelayEligibleConnections.Any(legacy =>
                    legacy.MachineId == session.MachineId && legacy.CompanyId == session.CompanyId),
                cancellationToken))
            return "active_session_without_routable_machine";

        if (await db.MachineSessions.AsNoTracking().AnyAsync(
                session => session.State == MachineSessionStates.ClosureIncomplete,
                cancellationToken))
            return "unresolved_session_closures";

        if (await db.TelemetryCommandDispatches.AsNoTracking().AnyAsync(
                dispatch => dispatch.Status == "Pending" || dispatch.Status == "Processing" ||
                            (dispatch.Operation != "MdbStatus" &&
                             (dispatch.Status == "Indeterminate" ||
                              (dispatch.Status == "Failed" &&
                               (dispatch.Error == "ack_timeout" || dispatch.Error == "command_outcome_unknown"))) &&
                             !db.TelemetryCommandResolutions.Any(resolution =>
                                 resolution.TelemetryCommandDispatchId == dispatch.Id)),
                cancellationToken))
            return "telemetry_commands_in_flight";

        // Commands sent through the socket owned by this process are audited here
        // rather than through TelemetryCommandDispatches. Persisted intents are
        // in flight; ACK timeouts are ambiguous because the device may have acted.
        if (await db.TelemetryCommands.AsNoTracking().AnyAsync(
                command => (command.Status == "accepted" && command.Error == "command_intent_persisted") ||
                           (command.Status == "failed" &&
                            (command.Error == "ack_timeout" || command.Error == "command_outcome_unknown") &&
                            !db.TelemetryCommandResolutions.Any(resolution =>
                                resolution.TelemetryCommandId == command.Id)),
                cancellationToken))
            return "telemetry_commands_in_flight";

        // The previous binary does not consume durable inbox rows. Do not declare
        // a release ready while accepted telemetry still needs processing or review.
        if (await db.DeviceMessageInbox.AsNoTracking().AnyAsync(
                message => message.Status != "Processed",
                cancellationToken))
            return "device_messages_unresolved";

        // Accepted provider webhooks are acknowledged after persistence. The previous
        // binary does not consume this inbox, so keep the release blocked until each
        // durable event is processed or explicitly retried to completion.
        if (await db.WebhookInboxMessages.AsNoTracking().AnyAsync(
                message => message.Status != "Processed",
                cancellationToken))
            return "payment_webhooks_unresolved";

        // The previous binary consumes Pending financial outbox rows but does not
        // reclaim Processing rows after a rollback. Wait for active leases to finish.
        if (await db.OutboxMessages.AsNoTracking().AnyAsync(
                message => message.Status == "processing",
                cancellationToken))
            return "financial_outbox_in_flight";

        if (legacyTelemetryRelay?.IsConfigured != true &&
            await db.OutboxMessages.AsNoTracking().AnyAsync(
                message => message.Status == "deferred_legacy_overlap",
                cancellationToken))
            return "legacy_overlap_outbox_not_activated";

        return null;
    }
}
