using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;

namespace VendingMachines.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class ClientsController : ControllerBase
{
    private readonly IDataService _dataService;

    public ClientsController(IDataService dataService)
    {
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

    private bool TryGetCompanyId(out Guid companyId)
    {
        var companyIdClaim = User.FindFirst("company_id")?.Value;
        return Guid.TryParse(companyIdClaim, out companyId);
    }
}
