using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;

namespace VendingMachines.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class ClientsController : BaseApiController
{
    private readonly IDataService _dataService;

    public ClientsController(IDataService dataService)
    {
        _dataService = dataService;
    }

    [HttpGet]
    public ActionResult<IEnumerable<LocationDto>> GetAll()
    {
        if (!TryGetEffectiveCompanyId(out var companyId))
        {
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });
        }

        return Ok(_dataService.GetLocations(companyId));
    }
}
