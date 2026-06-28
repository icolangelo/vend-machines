using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;

namespace VendingMachines.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class LocationsController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IDataService _dataService;

    public LocationsController(AppDbContext context, IDataService dataService)
    {
        _context = context;
        _dataService = dataService;
    }

    [HttpGet]
    public ActionResult<IEnumerable<LocationDto>> GetAll()
    {
        if (!TryGetCompanyId(out var companyId))
        {
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });
        }

        return Ok(_dataService.GetLocations(companyId));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        if (!TryGetCompanyId(out var companyId))
        {
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });
        }

        var location = await _context.Locations
            .FirstOrDefaultAsync(l => l.Id == id && l.CompanyId == companyId);

        if (location == null)
        {
            return NotFound(new { message = "Localização não encontrada." });
        }

        var machines = await _context.Machines
            .Where(m => m.LocationId == location.Id)
            .ToListAsync();

        return Ok(new LocationDto
        {
            Id = location.Id,
            CompanyId = location.CompanyId,
            Name = location.Name,
            MachineCount = machines.Count,
            Revenue30d = machines.Sum(m => m.Revenue30d),
            TotalSales30d = machines.Sum(m => m.TotalSales30d),
            CreatedAt = location.CreatedAt,
            UpdatedAt = location.UpdatedAt
        });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] LocationRequest request)
    {
        if (!TryGetCompanyId(out var companyId))
        {
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });
        }

        var name = request.Name.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return BadRequest(new { message = "O nome da localização é obrigatório." });
        }

        var alreadyExists = await _context.Locations
            .AnyAsync(l => l.CompanyId == companyId && l.Name.ToLower() == name.ToLower());

        if (alreadyExists)
        {
            return Conflict(new { message = "Já existe uma localização com este nome." });
        }

        var location = new Location
        {
            Id = Guid.NewGuid(),
            CompanyId = companyId,
            Name = name,
            CreatedAt = DateTime.UtcNow
        };

        _context.Locations.Add(location);
        await _context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetById), new { id = location.Id }, new LocationDto
        {
            Id = location.Id,
            CompanyId = location.CompanyId,
            Name = location.Name,
            CreatedAt = location.CreatedAt
        });
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] LocationRequest request)
    {
        if (!TryGetCompanyId(out var companyId))
        {
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });
        }

        var name = request.Name.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return BadRequest(new { message = "O nome da localização é obrigatório." });
        }

        var location = await _context.Locations
            .FirstOrDefaultAsync(l => l.Id == id && l.CompanyId == companyId);

        if (location == null)
        {
            return NotFound(new { message = "Localização não encontrada." });
        }

        var alreadyExists = await _context.Locations
            .AnyAsync(l => l.CompanyId == companyId && l.Id != id && l.Name.ToLower() == name.ToLower());

        if (alreadyExists)
        {
            return Conflict(new { message = "Já existe uma localização com este nome." });
        }

        location.Name = name;
        location.UpdatedAt = DateTime.UtcNow;

        var machines = await _context.Machines
            .Where(m => m.CompanyId == companyId && m.LocationId == location.Id)
            .ToListAsync();

        foreach (var machine in machines)
        {
            machine.ClientName = location.Name;
        }

        await _context.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        if (!TryGetCompanyId(out var companyId))
        {
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });
        }

        var location = await _context.Locations
            .FirstOrDefaultAsync(l => l.Id == id && l.CompanyId == companyId);

        if (location == null)
        {
            return NotFound(new { message = "Localização não encontrada." });
        }

        var hasMachines = await _context.Machines
            .AnyAsync(m => m.CompanyId == companyId && m.LocationId == location.Id);

        if (hasMachines)
        {
            return Conflict(new { message = "Não é possível excluir uma localização com máquinas vinculadas." });
        }

        _context.Locations.Remove(location);
        await _context.SaveChangesAsync();

        return NoContent();
    }

    private bool TryGetCompanyId(out Guid companyId)
    {
        var companyIdClaim = User.FindFirst("company_id")?.Value;
        return Guid.TryParse(companyIdClaim, out companyId);
    }
}

public class LocationRequest
{
    public string Name { get; set; } = string.Empty;
}
