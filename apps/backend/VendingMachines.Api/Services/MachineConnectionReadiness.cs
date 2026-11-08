using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;

namespace VendingMachines.Api.Services;

public static class MachineConnectionReadiness
{
    public static IQueryable<MachineConnectionState> GetLegacyRelayEligibleConnections(
        AppDbContext db,
        DateTime utcNow)
    {
        var freshnessCutoff = utcNow - MachineConnectionFreshness.Window;
        return db.MachineConnectionStates.AsNoTracking()
            .Where(state => state.IsOnline && state.LastSeenAt >= freshnessCutoff &&
                            state.ConnectionId != null && state.ConnectionId != "" &&
                            (state.OwnerNodeId == null || state.OwnerNodeId == "") &&
                            (state.OwnerConnectionId == null || state.OwnerConnectionId == "") &&
                            state.CommandRoutingVersion == null &&
                            db.Machines.Any(machine => machine.Id == state.MachineId &&
                                                       machine.CompanyId == state.CompanyId));
    }

    public static IQueryable<MachineSession> GetOpenSessionsWithoutRoutableConnection(
        AppDbContext db,
        DateTime utcNow)
    {
        var freshnessCutoff = utcNow - MachineConnectionFreshness.Window;
        return db.MachineSessions.AsNoTracking()
            .Where(session => session.ClosedAt == null &&
                              !db.MachineConnectionStates.AsNoTracking().Any(state =>
                                  state.MachineId == session.MachineId &&
                                  state.CompanyId == session.CompanyId &&
                                  state.IsOnline && state.LastSeenAt >= freshnessCutoff &&
                                  state.ConnectionId != null && state.ConnectionId != "" &&
                                  state.OwnerNodeId != null && state.OwnerNodeId != "" &&
                                  state.OwnerConnectionId == state.ConnectionId &&
                                  state.CommandRoutingVersion >= TelemetryConnectionManager.MinimumSupportedCommandRoutingVersion &&
                                  state.CommandRoutingVersion <= TelemetryConnectionManager.CurrentCommandRoutingVersion &&
                                  state.OwnerLeaseExpiresAt > utcNow &&
                                  db.Machines.Any(machine => machine.Id == state.MachineId &&
                                                             machine.CompanyId == state.CompanyId)));
    }

    public static IQueryable<MachineConnectionState> GetUnroutableRecentOnlineConnections(
        AppDbContext db,
        DateTime utcNow)
    {
        var freshnessCutoff = utcNow - MachineConnectionFreshness.Window;
        return db.MachineConnectionStates.AsNoTracking()
            .Where(state => state.IsOnline &&
                            (state.LastSeenAt == null || state.LastSeenAt >= freshnessCutoff) &&
                            (state.ConnectionId == null || state.ConnectionId == "" ||
                             state.OwnerNodeId == null || state.OwnerNodeId == "" ||
                             state.OwnerConnectionId != state.ConnectionId ||
                             state.CommandRoutingVersion == null ||
                             state.CommandRoutingVersion < TelemetryConnectionManager.MinimumSupportedCommandRoutingVersion ||
                             state.CommandRoutingVersion > TelemetryConnectionManager.CurrentCommandRoutingVersion ||
                             state.OwnerLeaseExpiresAt == null || state.OwnerLeaseExpiresAt <= utcNow ||
                             !db.Machines.Any(machine => machine.Id == state.MachineId &&
                                                         machine.CompanyId == state.CompanyId)));
    }
}
