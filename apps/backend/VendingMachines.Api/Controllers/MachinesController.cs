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

    public MachinesController(IDataService dataService, AppDbContext db)
    {
        _dataService = dataService;
        _db = db;
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
