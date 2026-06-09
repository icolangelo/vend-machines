using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
        return Ok(_dataService.GetMachines());
    }

    [HttpGet("{id}")]
    public ActionResult<Machine> GetById(string id)
    {
        var machine = _dataService.GetMachines().FirstOrDefault(m => m.Id == id);
        if (machine == null) return NotFound();
        return Ok(machine);
    }

    [HttpPost]
    public ActionResult Create([FromBody] Machine machine)
    {
        _dataService.AddMachine(machine);
        return CreatedAtAction(nameof(GetById), new { id = machine.Id }, machine);
    }

    [HttpPut("{id}")]
    public ActionResult Update(string id, [FromBody] Machine machine)
    {
        var existing = _dataService.GetMachines().FirstOrDefault(m => m.Id == id);
        if (existing == null) return NotFound();
        
        machine.Id = id; // Garantir que o ID corresponda à URL
        _dataService.UpdateMachine(machine);
        return NoContent();
    }
}
