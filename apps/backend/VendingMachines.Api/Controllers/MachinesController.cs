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
}
