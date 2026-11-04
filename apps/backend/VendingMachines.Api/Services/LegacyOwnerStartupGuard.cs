using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Data;

namespace VendingMachines.Api.Services;

/// <summary>
/// Prevents an active candidate from starting workers while recent machine sockets
/// have no safe route through either a valid owner lease or the private legacy relay.
/// </summary>
public static class LegacyOwnerStartupGuard
{
    public static async Task EnsureReadyBeforeWorkersAsync(
        AppDbContext db,
        LegacyTelemetryRequestRelay legacyTelemetryRelay,
        CancellationToken cancellationToken)
    {
        var utcNow = DateTime.UtcNow;
        var legacyConnections = MachineConnectionReadiness.GetLegacyRelayEligibleConnections(db, utcNow);
        var unroutableConnections = MachineConnectionReadiness.GetUnroutableRecentOnlineConnections(db, utcNow);
        if (await unroutableConnections.AnyAsync(
                connection => !legacyConnections.Any(legacy =>
                    legacy.MachineId == connection.MachineId && legacy.CompanyId == connection.CompanyId),
                cancellationToken))
        {
            throw new InvalidOperationException(
                "A API candidata não pode iniciar workers enquanto houver conexões MDB recentes sem owner/lease/rota válidos.");
        }

        if (!await legacyConnections.AnyAsync(cancellationToken))
            return;

        if (!legacyTelemetryRelay.IsConfigured ||
            !await legacyTelemetryRelay.IsLegacyApiReadyAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                "A API candidata não pode iniciar workers enquanto houver conexões MDB recentes sem relay privado saudável para a API anterior.");
        }
    }
}
