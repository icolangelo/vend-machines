using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Data;
using VendingMachines.Api.Services;

namespace VendingMachines.Api.Controllers;

[Authorize(Roles = AppRoles.PlatformAdmin)]
[ApiController]
[Route("api/admin/device-message-inbox")]
public sealed class DeviceMessageInboxController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly DeviceMessageInboxService _inboxService;
    private readonly IAuditLogService _auditLog;

    public DeviceMessageInboxController(
        AppDbContext db,
        DeviceMessageInboxService inboxService,
        IAuditLogService auditLog)
    {
        _db = db;
        _inboxService = inboxService;
        _auditLog = auditLog;
    }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] int page = 1, [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var inbox = _db.DeviceMessageInbox.AsNoTracking();
        var counts = await inbox.GroupBy(row => row.Status)
            .Select(group => new { status = group.Key, count = group.Count() })
            .ToListAsync(cancellationToken);
        var failedQuery = inbox.Where(row => row.Status == "Failed");
        var totalFailed = await failedQuery.CountAsync(cancellationToken);
        var failures = await failedQuery.OrderByDescending(row => row.ReceivedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(row => new
            {
                row.Id,
                row.MachineId,
                row.Serial,
                row.MessageId,
                row.ReceivedAt,
                row.Attempts,
                row.LastError
            })
            .ToListAsync(cancellationToken);

        return Ok(new
        {
            counts = counts.ToDictionary(item => item.status, item => item.count),
            failures,
            page,
            pageSize,
            totalFailed,
            totalPages = (int)Math.Ceiling(totalFailed / (double)pageSize)
        });
    }

    [HttpPost("{id:guid}/retry")]
    public async Task<IActionResult> Retry(Guid id, CancellationToken cancellationToken)
    {
        int updated;
        try
        {
            updated = await _inboxService.RequeueFailedAsync(id, cancellationToken);
        }
        catch (DeviceMessageInboxFullException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "device_message_inbox_full", message = "A fila está cheia; tente novamente quando houver capacidade." });
        }

        if (updated == 0)
        {
            var exists = await _db.DeviceMessageInbox.AnyAsync(row => row.Id == id, cancellationToken);
            return exists ? Conflict(new { error = "message_not_failed" }) : NotFound();
        }

        await _auditLog.RecordAsync(
            "telemetry.inbox.retried",
            "device_message_inbox",
            id.ToString("D"),
            metadata: new { source = "platform_admin_action" },
            cancellationToken: cancellationToken);
        return Accepted(new { id, status = "Pending" });
    }
}
