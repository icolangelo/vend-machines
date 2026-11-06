using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;
using VendingMachines.Api.Data;

namespace VendingMachines.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class ProductsController : BaseApiController
{
    private readonly IDataService _dataService;

    public ProductsController(IDataService dataService)
    {
        _dataService = dataService;
    }

    [HttpGet]
    public ActionResult<IEnumerable<FullProduct>> GetAll()
    {
        if (!TryGetEffectiveCompanyId(out var companyId))
        {
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });
        }
        return Ok(_dataService.GetProducts(companyId));
    }

    [HttpGet("top")]
    public ActionResult<IEnumerable<Product>> GetTop()
    {
        if (!TryGetEffectiveCompanyId(out var companyId))
        {
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });
        }

        return Ok(_dataService.GetTopProducts(companyId));
    }

    [HttpGet("bottom")]
    public ActionResult<IEnumerable<Product>> GetBottom()
    {
        if (!TryGetEffectiveCompanyId(out var companyId))
        {
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });
        }

        return Ok(_dataService.GetBottomProducts(companyId));
    }

    [HttpGet("types")]
    public ActionResult<IEnumerable<ProductType>> GetTypes()
    {
        if (!TryGetEffectiveCompanyId(out var companyId))
        {
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });
        }
        return Ok(_dataService.GetProductTypes(companyId));
    }

    [HttpPost]
    public ActionResult<FullProduct> CreateProduct([FromBody] FullProduct product)
    {
        if (!TryGetEffectiveCompanyId(out var companyId)) return BadRequest(new { message = "Usuário sem empresa." });
        
        product.CompanyId = companyId;
        try
        {
            var created = _dataService.AddProduct(product);
            return Ok(created);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPut("{id}")]
    public ActionResult<FullProduct> UpdateProduct(string id, [FromBody] FullProduct product)
    {
        if (!TryGetEffectiveCompanyId(out var companyId)) return BadRequest(new { message = "Usuário sem empresa." });

        product.Id = id;
        product.CompanyId = companyId;
        try
        {
            var updated = _dataService.UpdateProduct(product);
            return Ok(updated);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("types")]
    public ActionResult<ProductType> CreateProductType([FromBody] ProductType type)
    {
        if (!TryGetEffectiveCompanyId(out var companyId)) return BadRequest(new { message = "Usuário sem empresa." });

        type.CompanyId = companyId;
        try
        {
            var created = _dataService.AddProductType(type);
            return Ok(created);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPut("types/{id}")]
    public ActionResult<ProductType> UpdateProductType(string id, [FromBody] ProductType type)
    {
        if (!TryGetEffectiveCompanyId(out var companyId)) return BadRequest(new { message = "Usuário sem empresa." });

        type.Id = id;
        type.CompanyId = companyId;
        try
        {
            var updated = _dataService.UpdateProductType(type);
            return Ok(updated);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }



    [AllowAnonymous]
    [HttpGet("debug")]
    public ActionResult GetDebug()
    {
        var db = HttpContext.RequestServices.GetRequiredService<AppDbContext>();
        var types = db.ProductTypes.ToList();
        var prods = db.Products.ToList();
        return Ok(new {
            Types = types,
            Products = prods
        });
    }
}
