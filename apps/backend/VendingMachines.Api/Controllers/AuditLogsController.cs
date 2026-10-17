using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Data;
using VendingMachines.Api.Services;

namespace VendingMachines.Api.Controllers;

[Authorize(Roles = AppRoles.PlatformAdmin)]
[ApiController]
[Route("api/admin/audit-logs")]
public sealed class AuditLogsController : ControllerBase
{
    private readonly AppDbContext _db;

    public AuditLogsController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] Guid? companyId = null,
        [FromQuery] string? action = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var query = _db.AuditLogs.AsNoTracking().AsQueryable();
        if (companyId.HasValue) query = query.Where(x => x.CompanyId == companyId.Value);
        if (!string.IsNullOrWhiteSpace(action))
        {
            var normalizedAction = action.Trim();
            query = query.Where(x => x.Action == normalizedAction);
        }

        var totalItems = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(x => x.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return Ok(new
        {
            items,
            page,
            pageSize,
            totalItems,
            totalPages = (int)Math.Ceiling(totalItems / (double)pageSize)
        });
    }
}
