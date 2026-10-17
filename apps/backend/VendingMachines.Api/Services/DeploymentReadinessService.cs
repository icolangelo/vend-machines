using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Data;

namespace VendingMachines.Api.Services;

public sealed class DeploymentReadinessService(AppDbContext db)
{
    public async Task<string?> GetBlockerAsync(CancellationToken cancellationToken)
    {
        if (await db.MachineSessions.AsNoTracking().AnyAsync(
                session => session.ClosedAt == null,
                cancellationToken))
            return "active_vend_sessions";

        if (await db.TelemetryCommandDispatches.AsNoTracking().AnyAsync(
                dispatch => dispatch.Status == "Pending" || dispatch.Status == "Processing",
                cancellationToken))
            return "telemetry_commands_in_flight";

        return null;
    }
}
