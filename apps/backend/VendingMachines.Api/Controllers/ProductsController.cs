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
public class ProductsController : BaseApiController
{
    private readonly IDataService _dataService;
    private readonly AppDbContext _db;
    private readonly IAuditLogService? _audit;

    public ProductsController(IDataService dataService, AppDbContext db, IAuditLogService? audit = null)
    {
        _dataService = dataService;
        _db = db;
        _audit = audit;
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

    [Authorize(Roles = AppRoles.CompanyAdminOrPlatformAdmin)]
    [HttpPost]
    public async Task<ActionResult<FullProduct>> CreateProduct([FromBody] FullProduct product)
    {
        if (!TryGetEffectiveCompanyId(out var companyId)) return BadRequest(new { message = "Usuário sem empresa." });
        
        product.CompanyId = companyId;
        try
        {
            var created = _dataService.AddProduct(product);
            await RecordAuditAsync("product.created", "Product", created.Id, companyId);
            return Ok(created);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [Authorize(Roles = AppRoles.CompanyAdminOrPlatformAdmin)]
    [HttpPut("{id}")]
    public async Task<ActionResult<FullProduct>> UpdateProduct(string id, [FromBody] FullProduct product)
    {
        if (!TryGetEffectiveCompanyId(out var companyId)) return BadRequest(new { message = "Usuário sem empresa." });

        product.Id = id;
        product.CompanyId = companyId;
        try
        {
            var updated = _dataService.UpdateProduct(product);
            await RecordAuditAsync("product.updated", "Product", updated.Id, companyId);
            return Ok(updated);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [Authorize(Roles = AppRoles.CompanyAdminOrPlatformAdmin)]
    [HttpPost("types")]
    public async Task<ActionResult<ProductType>> CreateProductType([FromBody] ProductType type)
    {
        if (!TryGetEffectiveCompanyId(out var companyId)) return BadRequest(new { message = "Usuário sem empresa." });

        type.CompanyId = companyId;
        try
        {
            var created = _dataService.AddProductType(type);
            await RecordAuditAsync("product_type.created", "ProductType", created.Id, companyId);
            return Ok(created);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [Authorize(Roles = AppRoles.CompanyAdminOrPlatformAdmin)]
    [HttpPut("types/{id}")]
    public async Task<ActionResult<ProductType>> UpdateProductType(string id, [FromBody] ProductType type)
    {
        if (!TryGetEffectiveCompanyId(out var companyId)) return BadRequest(new { message = "Usuário sem empresa." });

        type.Id = id;
        type.CompanyId = companyId;
        try
        {
            var updated = _dataService.UpdateProductType(type);
            await RecordAuditAsync("product_type.updated", "ProductType", updated.Id, companyId);
            return Ok(updated);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [Authorize(Roles = AppRoles.CompanyAdminOrPlatformAdmin)]
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteProduct(string id, CancellationToken cancellationToken)
    {
        if (!TryGetEffectiveCompanyId(out var companyId))
            return BadRequest(new { message = "Usuário sem empresa." });

        var product = await _db.Products.FirstOrDefaultAsync(
            candidate => candidate.Id == id && candidate.CompanyId == companyId,
            cancellationToken);
        if (product == null) return NotFound(new { message = "Produto não encontrado nesta empresa." });

        var mappedToSlot = await _db.MachineProductSlots.AnyAsync(
            slot => slot.CompanyId == companyId && slot.ProductId == id,
            cancellationToken);
        if (mappedToSlot)
            return Conflict(new { message = "Remapeie os compartimentos antes de excluir este produto." });

        var hasSales = await _db.SaleRecords.AnyAsync(sale => sale.ProductId == id, cancellationToken);
        if (hasSales)
            return Conflict(new { message = "Este produto possui vendas históricas e não pode ser excluído." });

        var hasInventoryHistory = await _db.InventoryMovements.AnyAsync(movement => movement.ProductId == id, cancellationToken);
        if (hasInventoryHistory)
            return Conflict(new { message = "Este produto possui histórico de estoque e não pode ser excluído." });

        _db.Products.Remove(product);
        await _db.SaveChangesAsync(cancellationToken);
        await RecordAuditAsync("product.deleted", "Product", product.Id, companyId, cancellationToken);
        return NoContent();
    }

    private Task RecordAuditAsync(
        string action,
        string resourceType,
        string? resourceId,
        Guid companyId,
        CancellationToken? cancellationToken = null) =>
        _audit?.RecordAsync(
            action,
            resourceType,
            resourceId,
            companyId,
            cancellationToken: cancellationToken ?? HttpContext.RequestAborted)
        ?? Task.CompletedTask;
}
