using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Data;
using VendingMachines.Api.Services;

namespace VendingMachines.Api.Controllers;

[Authorize(Roles = AppRoles.PlatformAdmin)]
[ApiController]
[Route("api/admin/legacy-machine-connections")]
public sealed class LegacyMachineConnectionsController(
    AppDbContext db,
    LegacyTelemetryRequestRelay? legacyTelemetryRelay = null) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        pageSize = Math.Clamp(pageSize, 1, 200);
        var eligibleConnections = MachineConnectionReadiness.GetLegacyRelayEligibleConnections(db, DateTime.UtcNow);
        var totalItems = await eligibleConnections.CountAsync(cancellationToken);
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalItems / (double)pageSize));
        page = Math.Clamp(page, 1, totalPages);

        var relayConfigured = legacyTelemetryRelay?.IsConfigured == true;
        var relayHealthy = totalItems == 0
            ? (bool?)null
            : relayConfigured && await legacyTelemetryRelay!.IsLegacyApiReadyAsync(cancellationToken);

        var query =
            from state in eligibleConnections
            join machine in db.Machines.AsNoTracking()
                on state.MachineId equals machine.Id
            where machine.CompanyId == state.CompanyId
            join company in db.Companies.AsNoTracking()
                on state.CompanyId equals company.Id
            orderby state.LastSeenAt, machine.SerialNumber
            select new
            {
                machineId = machine.Id,
                machineName = machine.Name,
                serialNumber = machine.SerialNumber,
                companyName = company.Name,
                lastSeenAt = state.LastSeenAt,
                activeSessionState = db.MachineSessions.AsNoTracking()
                    .Where(session => session.MachineId == state.MachineId &&
                                      session.CompanyId == state.CompanyId &&
                                      session.ClosedAt == null)
                    .OrderByDescending(session => session.StartedAt)
                    .Select(session => session.State)
                    .FirstOrDefault(),
                activeSessionStartedAt = db.MachineSessions.AsNoTracking()
                    .Where(session => session.MachineId == state.MachineId &&
                                      session.CompanyId == state.CompanyId &&
                                      session.ClosedAt == null)
                    .OrderByDescending(session => session.StartedAt)
                    .Select(session => (DateTime?)session.StartedAt)
                    .FirstOrDefault()
            };

        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return Ok(new
        {
            generatedAt = DateTime.UtcNow,
            relay = new { configured = relayConfigured, healthy = relayHealthy },
            items,
            page,
            pageSize,
            totalItems,
            totalPages
        });
    }
}
