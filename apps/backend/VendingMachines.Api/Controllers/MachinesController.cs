using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;

namespace VendingMachines.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class MachinesController : ControllerBase
{
    private readonly IDataService _dataService;

    public MachinesController(IDataService dataService)
    {
        _dataService = dataService;
    }

    [HttpGet]
    public ActionResult<IEnumerable<Machine>> GetAll()
    {
        if (!TryGetCompanyId(out var companyId))
        {
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });
        }

        return Ok(_dataService.GetMachines(companyId));
    }

    [HttpGet("{id}")]
    public ActionResult<Machine> GetById(string id)
    {
        if (!TryGetCompanyId(out var companyId))
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
        if (!TryGetCompanyId(out var companyId))
        {
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });
        }

        machine.CompanyId = companyId;
        try
        {
            _dataService.AddMachine(machine);
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
        if (!TryGetCompanyId(out var companyId))
        {
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });
        }

        var existing = _dataService.GetMachines(companyId).FirstOrDefault(m => m.Id == id);
        if (existing == null) return NotFound();
        
        machine.CompanyId = companyId;
        machine.Id = id; // Garantir que o ID corresponda à URL
        try
        {
            _dataService.UpdateMachine(machine);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    private bool TryGetCompanyId(out Guid companyId)
    {
        var companyIdClaim = User.FindFirst("company_id")?.Value;
        return Guid.TryParse(companyIdClaim, out companyId);
    }
}
