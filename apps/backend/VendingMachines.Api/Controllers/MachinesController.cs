using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;

namespace VendingMachines.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class MachinesController : BaseApiController
{
    private readonly IDataService _dataService;
    private readonly AppDbContext _db;
    private readonly TelemetryConnectionManager _connections;
    private readonly IAuditLogService? _audit;

    public MachinesController(
        IDataService dataService,
        AppDbContext db,
        TelemetryConnectionManager connections,
        IAuditLogService? audit = null)
    {
        _dataService = dataService;
        _db = db;
        _connections = connections;
        _audit = audit;
    }

    [HttpGet]
    public ActionResult<IEnumerable<Machine>> GetAll()
    {
        if (!TryGetEffectiveCompanyId(out var companyId))
        {
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });
        }

        return Ok(_dataService.GetMachines(companyId));
    }

    [HttpGet("{id}")]
    public ActionResult<Machine> GetById(string id)
    {
        if (!TryGetEffectiveCompanyId(out var companyId))
        {
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });
        }

        var machine = _dataService.GetMachines(companyId).FirstOrDefault(m => m.Id == id);
        if (machine == null) return NotFound();
        return Ok(machine);
    }

    [Authorize(Roles = AppRoles.CompanyAdminOrPlatformAdmin)]
    [HttpPost]
    public ActionResult Create([FromBody] Machine machine)
    {
        if (!TryGetEffectiveCompanyId(out var companyId))
        {
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });
        }

        machine.CompanyId = companyId;
        try
        {
            using var transaction = _db.Database.BeginTransaction();
            _dataService.AddMachine(machine);
            ApplyAutomaticSessionConfiguration(machine, companyId, previousValue: null);
            _db.SaveChanges();
            transaction.Commit();
            machine.Company = null;
            machine.AssignedLocation = null;
            return CreatedAtAction(nameof(GetById), new { id = machine.Id }, machine);
        }
        catch (InvalidOperationException ex)
        {
            if (!ex.Message.Contains("ID"))
            {
                return BadRequest(new { message = ex.Message });
            }

            return Conflict(new { message = ex.Message });
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("PK_Machines") == true || ex.Message.Contains("PK_Machines"))
        {
            return Conflict(new { message = "Já existe uma máquina cadastrada com este ID." });
        }
    }

    [Authorize(Roles = AppRoles.CompanyAdminOrPlatformAdmin)]
    [HttpPut("{id}")]
    public ActionResult Update(string id, [FromBody] Machine machine)
    {
        if (!TryGetEffectiveCompanyId(out var companyId))
        {
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });
        }

        var existing = _dataService.GetMachines(companyId).FirstOrDefault(m => m.Id == id);
        if (existing == null) return NotFound();
        var previousAutoOpenValue = existing.AutoOpenSessionEnabled;
        
        machine.CompanyId = companyId;
        machine.Id = id; // Garantir que o ID corresponda à URL
        try
        {
            using var transaction = _db.Database.BeginTransaction();
            _dataService.UpdateMachine(machine);
            ApplyAutomaticSessionConfiguration(machine, companyId, previousAutoOpenValue);
            _db.SaveChanges();
            transaction.Commit();
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [Authorize(Roles = AppRoles.CompanyAdminOrPlatformAdmin)]
    [HttpPost("{id}/telemetry-credential")]
    public async Task<IActionResult> RotateTelemetryCredential(string id, CancellationToken cancellationToken)
    {
        if (!TryGetEffectiveCompanyId(out var companyId))
        {
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });
        }

        var machine = await _db.Machines.FirstOrDefaultAsync(
            x => x.Id == id && x.CompanyId == companyId,
            cancellationToken);
        if (machine == null) return NotFound();

        var credential = DeviceCredentialService.Generate();
        machine.TelemetryCredentialHash = DeviceCredentialService.Hash(credential);
        machine.TelemetryCredentialCreatedAt = DateTime.UtcNow;
        machine.TelemetryCredentialRevokedAt = null;
        await _db.SaveChangesAsync(cancellationToken);
        await _connections.DisconnectAsync(machine.SerialNumber, "Credencial rotacionada.");
        await RecordAuditAsync("machine.telemetry_credential_rotated", "Machine", machine.Id, companyId);

        return Ok(new
        {
            machineId = machine.Id,
            credential,
            createdAt = machine.TelemetryCredentialCreatedAt,
            message = "Guarde esta credencial. Ela será exibida somente agora."
        });
    }

    [Authorize(Roles = AppRoles.CompanyAdminOrPlatformAdmin)]
    [HttpDelete("{id}/telemetry-credential")]
    public async Task<IActionResult> RevokeTelemetryCredential(string id, CancellationToken cancellationToken)
    {
        if (!TryGetEffectiveCompanyId(out var companyId))
        {
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });
        }

        var machine = await _db.Machines.FirstOrDefaultAsync(
            x => x.Id == id && x.CompanyId == companyId,
            cancellationToken);
        if (machine == null) return NotFound();

        machine.TelemetryCredentialRevokedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        await _connections.DisconnectAsync(machine.SerialNumber, "Credencial revogada.");
        await RecordAuditAsync("machine.telemetry_credential_revoked", "Machine", machine.Id, companyId);
        return NoContent();
    }

    private Task RecordAuditAsync(string action, string resourceType, string resourceId, Guid companyId) =>
        _audit?.RecordAsync(action, resourceType, resourceId, companyId, cancellationToken: HttpContext.RequestAborted)
        ?? Task.CompletedTask;

    private void ApplyAutomaticSessionConfiguration(Machine machine, Guid companyId, bool? previousValue)
    {
        var hasChanged = previousValue.HasValue
            ? previousValue.Value != machine.AutoOpenSessionEnabled
            : machine.AutoOpenSessionEnabled;
        var userId = Guid.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var parsedUserId)
            ? parsedUserId
            : (Guid?)null;

        if (hasChanged)
        {
            _db.TelemetryEvents.Add(new TelemetryEvent
            {
                CompanyId = companyId,
                MachineId = machine.Id,
                UserId = userId,
                EventType = machine.AutoOpenSessionEnabled
                    ? "machine.auto_open.enabled"
                    : "machine.auto_open.disabled"
            });
        }

        var state = _db.MachineConnectionStates.FirstOrDefault(x => x.MachineId == machine.Id);
        if (state != null && state.CompanyId != companyId)
        {
            if (hasChanged && machine.AutoOpenSessionEnabled)
            {
                throw new InvalidOperationException(
                    "Não foi possível ativar o acompanhamento desta máquina. Atualize o estado de telemetria e tente novamente.");
            }

            // Never change monitoring preferences or telemetry errors owned by another company.
            return;
        }

        if (!machine.AutoOpenSessionEnabled)
        {
            if (state != null)
            {
                state.AutoOpenLastError = null;
            }
            return;
        }

        if (state == null)
        {
            state = new MachineConnectionState
            {
                MachineId = machine.Id,
                CompanyId = companyId
            };
            _db.MachineConnectionStates.Add(state);
        }

        if (state.MonitoringEnabled)
        {
            return;
        }

        state.MonitoringEnabled = true;
        state.ActivatedAt = DateTime.UtcNow;
        state.ActivatedByUserId = userId;
        _db.TelemetryEvents.Add(new TelemetryEvent
        {
            CompanyId = companyId,
            MachineId = machine.Id,
            UserId = state.ActivatedByUserId,
            EventType = "monitoring.activated",
            Detail = "Acompanhamento ativado junto com a abertura automática."
        });
    }

}
