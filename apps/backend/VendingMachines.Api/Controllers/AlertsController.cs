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
    [HttpGet]
    public async Task<IActionResult> GetAlerts(
        [FromQuery] string status = "Active",
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
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

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(alert => alert.Status == "Active")
            .ThenByDescending(alert => alert.Severity == "critical")
            .ThenByDescending(alert => alert.UpdatedAt)
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
