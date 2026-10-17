using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;

namespace VendingMachines.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/inventory")]
public sealed class InventoryController : BaseApiController
{
    private readonly AppDbContext _db;
    private readonly IAuditLogService _audit;

    public InventoryController(AppDbContext db, IAuditLogService audit)
    {
        _db = db;
        _audit = audit;
    }

    [HttpGet("slots")]
    public async Task<IActionResult> GetSlots([FromQuery] string? machineId = null, CancellationToken cancellationToken = default)
    {
        if (!TryGetEffectiveCompanyId(out var companyId))
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });

        var query = _db.MachineProductSlots.AsNoTracking().Where(slot =>
            slot.CompanyId == companyId &&
            _db.Machines.Any(machine => machine.Id == slot.MachineId && machine.CompanyId == companyId));
        if (!string.IsNullOrWhiteSpace(machineId)) query = query.Where(slot => slot.MachineId == machineId);

        var slots = await query.OrderBy(slot => slot.MachineId).ThenBy(slot => slot.ItemNumber)
            .Select(slot => new
            {
                slot.Id,
                slot.MachineId,
                MachineName = _db.Machines.Where(machine => machine.Id == slot.MachineId && machine.CompanyId == companyId)
                    .Select(machine => machine.Name).FirstOrDefault(),
                slot.ItemNumber,
                slot.ProductId,
                ProductName = _db.Products.Where(product => product.Id == slot.ProductId &&
                    (product.CompanyId == null || product.CompanyId == companyId))
                    .Select(product => product.Name).FirstOrDefault(),
                slot.Capacity,
                slot.CurrentStock,
                slot.LowStockThreshold,
                IsLowStock = slot.CurrentStock <= slot.LowStockThreshold,
                slot.CreatedAt,
                slot.UpdatedAt
            })
            .ToListAsync(cancellationToken);

        return Ok(slots);
    }

    [Authorize(Roles = AppRoles.CompanyAdminOrPlatformAdmin)]
    [HttpPut("slots/{machineId}/{itemNumber:int}")]
    public async Task<IActionResult> ConfigureSlot(
        string machineId,
        int itemNumber,
        [FromBody] ConfigureMachineSlotRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryGetEffectiveCompanyId(out var companyId))
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });
        if (itemNumber < 0 || itemNumber > 9999 || request.Capacity <= 0 || request.Capacity > 100000 || request.InitialStock < 0 ||
            request.InitialStock > request.Capacity || request.LowStockThreshold < 0 ||
            request.LowStockThreshold > request.Capacity || string.IsNullOrWhiteSpace(request.ProductId))
            return BadRequest(new { message = "Revise produto, capacidade, saldo inicial e limite de reposição." });

        var machineExists = await _db.Machines.AnyAsync(
            machine => machine.Id == machineId && machine.CompanyId == companyId, cancellationToken);
        if (!machineExists) return NotFound(new { message = "Máquina não encontrada." });

        var product = await _db.Products.AsNoTracking().FirstOrDefaultAsync(
            candidate => candidate.Id == request.ProductId &&
                         (candidate.CompanyId == null || candidate.CompanyId == companyId),
            cancellationToken);
        if (product == null) return BadRequest(new { message = "Produto não encontrado nesta empresa." });

        var slot = await _db.MachineProductSlots.FirstOrDefaultAsync(
            candidate => candidate.CompanyId == companyId && candidate.MachineId == machineId &&
                         candidate.ItemNumber == itemNumber,
            cancellationToken);
        var now = DateTime.UtcNow;
        if (slot == null)
        {
            slot = new MachineProductSlot
            {
                CompanyId = companyId,
                MachineId = machineId,
                ItemNumber = itemNumber,
                ProductId = product.Id,
                Capacity = request.Capacity,
                CurrentStock = request.InitialStock,
                LowStockThreshold = request.LowStockThreshold,
                CreatedAt = now,
                UpdatedAt = now
            };
            _db.MachineProductSlots.Add(slot);
            if (request.InitialStock > 0)
            {
                _db.InventoryMovements.Add(new InventoryMovement
                {
                    CompanyId = companyId,
                    MachineId = machineId,
                    ItemNumber = itemNumber,
                    ProductId = product.Id,
                    MovementType = "OpeningBalance",
                    QuantityDelta = request.InitialStock,
                    QuantityAfter = request.InitialStock,
                    Reason = "Saldo inicial do compartimento.",
                    UserId = GetCurrentUserId(),
                    CreatedAt = now
                });
            }
        }
        else
        {
            if (request.InitialStock != slot.CurrentStock)
                return Conflict(new { message = "Use as ações de reposição ou ajuste para alterar o saldo atual." });
            if (request.Capacity < slot.CurrentStock)
                return Conflict(new { message = "A capacidade não pode ser menor que o saldo atual." });
            slot.ProductId = product.Id;
            slot.Capacity = request.Capacity;
            slot.LowStockThreshold = request.LowStockThreshold;
            slot.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(cancellationToken);
        await _audit.RecordAsync(
            "inventory.slot_configured", "MachineProductSlot", slot.Id.ToString(), companyId,
            new { machineId, itemNumber, productId = product.Id, capacity = slot.Capacity }, cancellationToken);
        return Ok(new { slot.Id, slot.MachineId, slot.ItemNumber, slot.ProductId, slot.Capacity, slot.CurrentStock, slot.LowStockThreshold });
    }

    [Authorize(Roles = AppRoles.CompanyAdminOrPlatformAdmin)]
    [HttpPost("slots/{machineId}/{itemNumber:int}/restock")]
    public Task<IActionResult> Restock(
        string machineId,
        int itemNumber,
        [FromBody] InventoryQuantityRequest request,
        CancellationToken cancellationToken) =>
        ApplyMovementAsync(machineId, itemNumber, request.Quantity, "Restock", request.Reason, cancellationToken);

    [Authorize(Roles = AppRoles.CompanyAdminOrPlatformAdmin)]
    [HttpPost("slots/{machineId}/{itemNumber:int}/adjustment")]
    public Task<IActionResult> AdjustStock(
        string machineId,
        int itemNumber,
        [FromBody] InventoryAdjustmentRequest request,
        CancellationToken cancellationToken) =>
        ApplyMovementAsync(machineId, itemNumber, request.QuantityDelta, "Adjustment", request.Reason, cancellationToken);

    [HttpGet("movements")]
    public async Task<IActionResult> GetMovements(
        [FromQuery] string? machineId = null,
        [FromQuery] int? itemNumber = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetEffectiveCompanyId(out var companyId))
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var query = _db.InventoryMovements.AsNoTracking().Where(movement =>
            movement.CompanyId == companyId &&
            _db.Machines.Any(machine => machine.Id == movement.MachineId && machine.CompanyId == companyId));
        if (!string.IsNullOrWhiteSpace(machineId)) query = query.Where(movement => movement.MachineId == machineId);
        if (itemNumber.HasValue) query = query.Where(movement => movement.ItemNumber == itemNumber.Value);
        var totalItems = await query.CountAsync(cancellationToken);
        var movements = await query.OrderByDescending(movement => movement.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(movement => new
            {
                movement.Id,
                movement.MachineId,
                movement.ItemNumber,
                movement.ProductId,
                ProductName = _db.Products.Where(product => product.Id == movement.ProductId &&
                    (product.CompanyId == null || product.CompanyId == companyId))
                    .Select(product => product.Name).FirstOrDefault(),
                movement.MovementType,
                movement.QuantityDelta,
                movement.QuantityAfter,
                movement.Reason,
                movement.UserId,
                movement.CreatedAt
            }).ToListAsync(cancellationToken);
        return Ok(new { items = movements, page, pageSize, totalItems, totalPages = (int)Math.Ceiling(totalItems / (double)pageSize) });
    }

    private async Task<IActionResult> ApplyMovementAsync(
        string machineId,
        int itemNumber,
        int quantityDelta,
        string movementType,
        string? reason,
        CancellationToken cancellationToken)
    {
        if (!TryGetEffectiveCompanyId(out var companyId))
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });
        if (itemNumber < 0 || itemNumber > 9999 || quantityDelta == 0 || quantityDelta is > 100000 or < -100000 ||
            string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 500 ||
            (movementType == "Restock" && quantityDelta < 0))
            return BadRequest(new { message = "Informe uma quantidade válida e o motivo da movimentação." });

        var machineExists = await _db.Machines.AnyAsync(
            machine => machine.Id == machineId && machine.CompanyId == companyId,
            cancellationToken);
        if (!machineExists) return NotFound(new { message = "Máquina não encontrada." });

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        var query = _db.MachineProductSlots.Where(slot => slot.CompanyId == companyId &&
            slot.MachineId == machineId && slot.ItemNumber == itemNumber);
        var now = DateTime.UtcNow;
        var affected = await query
            .Where(slot => slot.CurrentStock + quantityDelta >= 0 && slot.CurrentStock + quantityDelta <= slot.Capacity)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(slot => slot.CurrentStock, slot => slot.CurrentStock + quantityDelta)
                .SetProperty(slot => slot.UpdatedAt, now), cancellationToken);
        if (affected == 0)
        {
            var slotExists = await query.AnyAsync(cancellationToken);
            if (!slotExists) return NotFound(new { message = "Compartimento não encontrado." });
            return Conflict(new { message = "A movimentação deixaria o saldo fora dos limites de capacidade." });
        }

        var slotState = await query.Select(slot => new { slot.ProductId, slot.CurrentStock }).SingleAsync(cancellationToken);
        _db.InventoryMovements.Add(new InventoryMovement
        {
            CompanyId = companyId,
            MachineId = machineId,
            ItemNumber = itemNumber,
            ProductId = slotState.ProductId,
            UserId = GetCurrentUserId(),
            MovementType = movementType,
            QuantityDelta = quantityDelta,
            QuantityAfter = slotState.CurrentStock,
            Reason = reason.Trim(),
            CreatedAt = now
        });
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await _audit.RecordAsync(
            movementType == "Restock" ? "inventory.restocked" : "inventory.adjusted",
            "MachineProductSlot", $"{machineId}:{itemNumber}", companyId,
            new { quantityDelta, quantityAfter = slotState.CurrentStock, reason = reason.Trim() }, cancellationToken);
        return Ok(new { machineId, itemNumber, stock = slotState.CurrentStock, movementType });
    }

    private Guid? GetCurrentUserId() => Guid.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId)
        ? userId
        : null;
}

public sealed record ConfigureMachineSlotRequest(string ProductId, int Capacity, int InitialStock, int LowStockThreshold);
public sealed record InventoryQuantityRequest(int Quantity, string Reason);
public sealed record InventoryAdjustmentRequest(int QuantityDelta, string Reason);
