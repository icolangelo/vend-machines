using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;

namespace VendingMachines.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class DashboardController : BaseApiController
{
    private readonly IDataService _dataService;

    public DashboardController(IDataService dataService)
    {
        _dataService = dataService;
    }

    [HttpGet("stats")]
    public ActionResult<DashboardStats> GetStats()
    {
        if (!TryGetEffectiveCompanyId(out var companyId))
        {
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });
        }

        return Ok(_dataService.GetDashboardStats(companyId));
    }


}
