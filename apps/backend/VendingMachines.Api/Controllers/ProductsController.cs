using Microsoft.AspNetCore.Mvc;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;

namespace VendingMachines.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ProductsController : ControllerBase
{
    private readonly IDataService _dataService;

    public ProductsController(IDataService dataService)
    {
        _dataService = dataService;
    }

    [HttpGet]
    public ActionResult<IEnumerable<FullProduct>> GetAll()
    {
        return Ok(_dataService.GetProducts());
    }

    [HttpGet("top")]
    public ActionResult<IEnumerable<Product>> GetTop()
    {
        return Ok(_dataService.GetTopProducts());
    }

    [HttpGet("bottom")]
    public ActionResult<IEnumerable<Product>> GetBottom()
    {
        return Ok(_dataService.GetBottomProducts());
    }

    [HttpGet("types")]
    public ActionResult<IEnumerable<ProductType>> GetTypes()
    {
        return Ok(_dataService.GetProductTypes());
    }
}
