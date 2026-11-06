using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;

namespace VendingMachines.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/alerts")]
public sealed class AlertsController(AppDbContext db) : BaseApiController
{
    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary(CancellationToken cancellationToken = default)
    {
        if (!TryGetEffectiveCompanyId(out var companyId))
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });

        var activeAlerts = db.OperationalAlerts.AsNoTracking()
            .Where(alert => alert.CompanyId == companyId && alert.Status == "Active");
        var disconnectedMachines = await activeAlerts
            .Where(alert => alert.Type == "TelemetryOffline" && alert.MachineId != null &&
                db.Machines.Any(machine => machine.Id == alert.MachineId && machine.CompanyId == companyId))
            .Select(alert => alert.MachineId)
            .Distinct()
            .CountAsync(cancellationToken);
        var otherActiveAlerts = await activeAlerts
            .CountAsync(alert => alert.Type != "TelemetryOffline", cancellationToken);

        return Ok(new { DisconnectedMachines = disconnectedMachines, OtherActiveAlerts = otherActiveAlerts });
    }

    [HttpGet]
    public async Task<IActionResult> GetAlerts(
        [FromQuery] string status = "Active",
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default,
        [FromQuery] string type = "All",
        [FromQuery] string? search = null)
    {
        if (!TryGetEffectiveCompanyId(out var companyId))
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return Unauthorized();

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var query = db.OperationalAlerts.AsNoTracking().Where(alert => alert.CompanyId == companyId);
        if (status is "Active" or "Resolved") query = query.Where(alert => alert.Status == status);
        else if (status != "All") return BadRequest(new { message = "Status inválido. Use Active, Resolved ou All." });
        if (type == "TelemetryOffline") query = query.Where(alert => alert.Type == "TelemetryOffline");
        else if (type == "OtherActive") query = query.Where(alert => alert.Status == "Active" && alert.Type != "TelemetryOffline");
        else if (type != "All") return BadRequest(new { message = "Tipo inválido. Use TelemetryOffline, OtherActive ou All." });

        var searchTerm = search?.Trim();
        if (!string.IsNullOrEmpty(searchTerm))
        {
            query = query.Where(alert =>
                alert.Title.Contains(searchTerm) ||
                alert.Description.Contains(searchTerm) ||
                (alert.MachineId != null && db.Machines.Any(machine =>
                    machine.Id == alert.MachineId && machine.CompanyId == companyId &&
                    (machine.Id.Contains(searchTerm) || machine.SerialNumber.Contains(searchTerm) ||
                     machine.Name.Contains(searchTerm) || (machine.ClientName != null && machine.ClientName.Contains(searchTerm))))));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(alert => alert.Status == "Active")
            .ThenByDescending(alert => alert.Severity == "critical")
            .ThenByDescending(alert => alert.UpdatedAt)
            .ThenBy(alert => alert.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(alert => new
            {
                alert.Id,
                alert.MachineId,
                MachineName = db.Machines.Where(machine => machine.Id == alert.MachineId && machine.CompanyId == companyId)
                    .Select(machine => machine.Name).FirstOrDefault(),
                ClientName = db.Machines.Where(machine => machine.Id == alert.MachineId && machine.CompanyId == companyId)
                    .Select(machine => machine.ClientName).FirstOrDefault(),
                alert.SessionId,
                SessionState = alert.SessionId == null
                    ? null
                    : db.MachineSessions.Where(session => session.Id == alert.SessionId && session.CompanyId == companyId)
                        .Select(session => session.State).FirstOrDefault(),
                alert.TransactionId,
                alert.Type,
                alert.Severity,
                alert.Status,
                alert.Title,
                alert.Description,
                alert.OccurredAt,
                alert.UpdatedAt,
                alert.ResolvedAt,
                alert.Resolution,
                IsRead = db.AlertReadReceipts.Any(receipt => receipt.AlertId == alert.Id && receipt.UserId == userId)
            })
            .ToListAsync(cancellationToken);

        return Ok(new { items, total, page, pageSize });
    }

    [HttpPut("{id:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetEffectiveCompanyId(out var companyId))
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return Unauthorized();

        var belongsToCompany = await db.OperationalAlerts.AnyAsync(
            alert => alert.Id == id && alert.CompanyId == companyId, cancellationToken);
        if (!belongsToCompany) return NotFound(new { message = "Alerta não encontrado." });

        var receipt = await db.AlertReadReceipts.FindAsync([id, userId], cancellationToken);
        if (receipt is null)
        {
            db.AlertReadReceipts.Add(new AlertReadReceipt { AlertId = id, UserId = userId, ReadAt = DateTime.UtcNow });
            await db.SaveChangesAsync(cancellationToken);
        }

        return NoContent();
    }
}
