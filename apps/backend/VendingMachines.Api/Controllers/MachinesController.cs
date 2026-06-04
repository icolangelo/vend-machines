using Microsoft.AspNetCore.Mvc;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;

namespace VendingMachines.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class MachinesController : ControllerBase
{
    private readonly IMockDataService _mockDataService;

    public MachinesController(IMockDataService mockDataService)
    {
        _mockDataService = mockDataService;
    }

    [HttpGet]
    public ActionResult<IEnumerable<Machine>> GetAll()
    {
        return Ok(_mockDataService.GetMachines());
    }

    [HttpGet("{id}")]
    public ActionResult<Machine> GetById(string id)
    {
        var machine = _mockDataService.GetMachines().FirstOrDefault(m => m.Id == id);
        if (machine == null) return NotFound();
        return Ok(machine);
    }

    [HttpPost]
    public ActionResult Create([FromBody] Machine machine)
    {
        _mockDataService.AddMachine(machine);
        return CreatedAtAction(nameof(GetById), new { id = machine.Id }, machine);
    }

    [HttpPut("{id}")]
    public ActionResult Update(string id, [FromBody] Machine machine)
    {
        var existing = _mockDataService.GetMachines().FirstOrDefault(m => m.Id == id);
        if (existing == null) return NotFound();
        
        machine.Id = id; // Ensure ID matches URL
        _mockDataService.UpdateMachine(machine);
        return NoContent();
    }
}
