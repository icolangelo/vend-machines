using Microsoft.AspNetCore.Mvc;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;

namespace VendingMachines.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ProductsController : ControllerBase
{
    private readonly IMockDataService _mockDataService;

    public ProductsController(IMockDataService mockDataService)
    {
        _mockDataService = mockDataService;
    }

    [HttpGet]
    public ActionResult<IEnumerable<FullProduct>> GetAll()
    {
        return Ok(_mockDataService.GetProducts());
    }

    [HttpGet("top")]
    public ActionResult<IEnumerable<Product>> GetTop()
    {
        return Ok(_mockDataService.GetTopProducts());
    }

    [HttpGet("bottom")]
    public ActionResult<IEnumerable<Product>> GetBottom()
    {
        return Ok(_mockDataService.GetBottomProducts());
    }

    [HttpGet("types")]
    public ActionResult<IEnumerable<ProductType>> GetTypes()
    {
        return Ok(_mockDataService.GetProductTypes());
    }
}
