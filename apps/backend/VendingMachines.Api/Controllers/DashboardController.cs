using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;

namespace VendingMachines.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class DashboardController : ControllerBase
{
    private readonly IDataService _dataService;

    public DashboardController(IDataService dataService)
    {
        _dataService = dataService;
    }

    [HttpGet("stats")]
    public ActionResult<DashboardStats> GetStats()
    {
        return Ok(_dataService.GetDashboardStats());
    }
}
