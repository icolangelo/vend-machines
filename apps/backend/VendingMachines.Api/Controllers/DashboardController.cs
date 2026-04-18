using Microsoft.AspNetCore.Mvc;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;

namespace VendingMachines.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DashboardController : ControllerBase
{
    private readonly IMockDataService _mockDataService;

    public DashboardController(IMockDataService mockDataService)
    {
        _mockDataService = mockDataService;
    }

    [HttpGet("stats")]
    public ActionResult<DashboardStats> GetStats()
    {
        return Ok(_mockDataService.GetDashboardStats());
    }
}
