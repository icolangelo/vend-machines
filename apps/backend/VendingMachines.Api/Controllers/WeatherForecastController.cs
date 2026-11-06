using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Data;

namespace VendingMachines.Api.Controllers;

[ApiController]
[Route("[controller]")]
public class WeatherForecastController : ControllerBase
{
    private static readonly string[] Summaries = new[]
    {
        "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
    };

    private readonly ILogger<WeatherForecastController> _logger;
    private readonly AppDbContext _context;

    public WeatherForecastController(ILogger<WeatherForecastController> logger, AppDbContext context)
    {
        _logger = logger;
        _context = context;
    }

    [HttpGet(Name = "GetWeatherForecast")]
    public IEnumerable<WeatherForecast> Get()
    {
        return Enumerable.Range(1, 5).Select(index => new WeatherForecast
        {
            Date = DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            TemperatureC = Random.Shared.Next(-20, 55),
            Summary = Summaries[Random.Shared.Next(Summaries.Length)]
        })
        .ToArray();
    }

    [HttpGet("db-diagnostic")]
    public async Task<IActionResult> GetDbDiagnostic()
    {
        try
        {
            var pending = await _context.Database.GetPendingMigrationsAsync();
            var applied = await _context.Database.GetAppliedMigrationsAsync();
            var all = _context.Database.GetMigrations();
            
            return Ok(new {
                Provider = _context.Database.ProviderName,
                Pending = pending,
                Applied = applied,
                All = all
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Error = ex.ToString() });
        }
    }

    [HttpGet("run-migration")]
    public async Task<IActionResult> RunMigration()
    {
        try
        {
            await _context.Database.MigrateAsync();
            return Ok(new { Message = "Migration ran successfully!" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { Error = ex.ToString() });
        }
    }
}
