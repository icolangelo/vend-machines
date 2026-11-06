using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
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

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        if (!await MachineOperationLock.AcquireAsync(_db, machineId, companyId, cancellationToken))
            return NotFound(new { message = "Máquina não encontrada." });
        if (await HasActiveSessionAsync(machineId, companyId, cancellationToken))
            return Conflict(new { code = "active_vend_session", message = "Finalize a sessão MDB ativa antes de alterar a configuração do estoque." });

        var product = await _db.Products.AsNoTracking().FirstOrDefaultAsync(
            candidate => candidate.Id == request.ProductId &&
                         (candidate.CompanyId == null || candidate.CompanyId == companyId),
            cancellationToken);
        if (product == null) return BadRequest(new { message = "Produto não encontrado nesta empresa." });

        var slot = await _db.MachineProductSlots.AsNoTracking().FirstOrDefaultAsync(
            candidate => candidate.CompanyId == companyId && candidate.MachineId == machineId &&
                         candidate.ItemNumber == itemNumber,
            cancellationToken);
        var now = DateTime.UtcNow;
        var creatingSlot = slot == null;
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

            var affected = await _db.MachineProductSlots
                .Where(candidate => candidate.CompanyId == companyId && candidate.MachineId == machineId &&
                                    candidate.ItemNumber == itemNumber &&
                                    candidate.CurrentStock == request.InitialStock &&
                                    candidate.CurrentStock <= request.Capacity)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(candidate => candidate.ProductId, product.Id)
                    .SetProperty(candidate => candidate.Capacity, request.Capacity)
                    .SetProperty(candidate => candidate.LowStockThreshold, request.LowStockThreshold)
                    .SetProperty(candidate => candidate.UpdatedAt, now), cancellationToken);
            if (affected == 0)
            {
                var currentStock = await _db.MachineProductSlots.AsNoTracking()
                    .Where(candidate => candidate.CompanyId == companyId && candidate.MachineId == machineId &&
                                        candidate.ItemNumber == itemNumber)
                    .Select(candidate => (int?)candidate.CurrentStock)
                    .SingleOrDefaultAsync(cancellationToken);
                if (!currentStock.HasValue)
                    return NotFound(new { message = "Compartimento não encontrado." });
                return Conflict(new
                {
                    message = currentStock.Value != request.InitialStock
                        ? "O saldo do compartimento mudou durante a configuração. Atualize os dados antes de tentar novamente."
                        : "A capacidade não pode ser menor que o saldo atual."
                });
            }

            slot.ProductId = product.Id;
            slot.Capacity = request.Capacity;
            slot.LowStockThreshold = request.LowStockThreshold;
            slot.UpdatedAt = now;
        }

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException) when (creatingSlot)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            await transaction.DisposeAsync();
            _db.ChangeTracker.Clear();
            var slotWasCreatedConcurrently = await _db.MachineProductSlots.AsNoTracking().AnyAsync(
                candidate => candidate.CompanyId == companyId && candidate.MachineId == machineId &&
                             candidate.ItemNumber == itemNumber,
                cancellationToken);
            if (slotWasCreatedConcurrently)
                return Conflict(new { message = "O compartimento foi configurado por outra operação. Atualize os dados antes de tentar novamente." });
            throw;
        }

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
        ApplyMovementAsync(machineId, itemNumber, request.Quantity, "Restock", request.Reason, request.RequestId, cancellationToken);

    [Authorize(Roles = AppRoles.CompanyAdminOrPlatformAdmin)]
    [HttpPost("slots/{machineId}/{itemNumber:int}/adjustment")]
    public Task<IActionResult> AdjustStock(
        string machineId,
        int itemNumber,
        [FromBody] InventoryAdjustmentRequest request,
        CancellationToken cancellationToken) =>
        ApplyMovementAsync(machineId, itemNumber, request.QuantityDelta, "Adjustment", request.Reason, request.RequestId, cancellationToken);

    [Authorize(Roles = AppRoles.CompanyAdminOrPlatformAdmin)]
    [HttpGet("movements/by-request/{requestId:guid}")]
    public async Task<IActionResult> GetMovementByRequest(Guid requestId, CancellationToken cancellationToken)
    {
        if (!TryGetEffectiveCompanyId(out var companyId))
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });
        if (requestId == Guid.Empty || GetCurrentUserId() is not Guid userId)
            return NotFound();

        var movementId = MovementIdForRequest(companyId, requestId);
        var movement = await _db.InventoryMovements.AsNoTracking()
            .Where(item => item.Id == movementId && item.CompanyId == companyId && item.UserId == userId &&
                (item.MovementType == "Restock" || item.MovementType == "Adjustment") &&
                _db.Machines.Any(machine => machine.Id == item.MachineId && machine.CompanyId == companyId))
            .Select(item => new { item.MachineId, item.ItemNumber, item.MovementType, item.QuantityDelta, item.Reason })
            .SingleOrDefaultAsync(cancellationToken);
        if (movement is null) return NotFound();

        return Ok(new
        {
            movement.MachineId, movement.ItemNumber, movement.MovementType, movement.QuantityDelta,
            reasonHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(movement.Reason))).ToLowerInvariant()
        });
    }

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
        var query = BuildMovementQuery(companyId, machineId, itemNumber);
        var totalItems = await query.CountAsync(cancellationToken);
        var movements = await query.OrderByDescending(movement => movement.CreatedAt).ThenByDescending(movement => movement.Id)
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

    [HttpGet("movements/export")]
    public async Task<IActionResult> ExportMovements(
        [FromQuery] string? machineId = null,
        [FromQuery] int? itemNumber = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetEffectiveCompanyId(out var companyId))
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });

        Response.ContentType = "text/csv; charset=utf-8";
        Response.Headers["Content-Disposition"] = "attachment; filename=movimentacoes-estoque.csv";
        Response.Headers["Cache-Control"] = "no-store";

        await using var writer = new StreamWriter(Response.Body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 4096, leaveOpen: true);
        await writer.WriteAsync("Data UTC;ID da máquina;Compartimento;Produto;Tipo;Variação;Saldo;Motivo\r\n".AsMemory(), cancellationToken);

        var rows = BuildMovementQuery(companyId, machineId, itemNumber)
            .OrderByDescending(movement => movement.CreatedAt)
            .ThenByDescending(movement => movement.Id)
            .Select(movement => new
            {
                movement.CreatedAt,
                movement.MachineId,
                movement.ItemNumber,
                movement.ProductId,
                ProductName = _db.Products.Where(product => product.Id == movement.ProductId &&
                    (product.CompanyId == null || product.CompanyId == companyId))
                    .Select(product => product.Name).FirstOrDefault(),
                movement.MovementType,
                movement.QuantityDelta,
                movement.QuantityAfter,
                movement.Reason
            })
            .AsAsyncEnumerable();

        var rowsWritten = 0;
        await foreach (var movement in rows.WithCancellation(cancellationToken))
        {
            var createdAtUtc = DateTime.SpecifyKind(movement.CreatedAt, DateTimeKind.Utc)
                .ToString("O", CultureInfo.InvariantCulture);
            var cells = new[]
            {
                CsvCell(createdAtUtc),
                CsvCell(movement.MachineId),
                movement.ItemNumber.ToString(CultureInfo.InvariantCulture),
                CsvCell(movement.ProductName ?? movement.ProductId),
                CsvCell(InventoryMovementLabel(movement.MovementType)),
                CsvCell(movement.QuantityDelta.ToString(CultureInfo.InvariantCulture)),
                CsvCell(movement.QuantityAfter.ToString(CultureInfo.InvariantCulture)),
                CsvCell(movement.Reason)
            };
            await writer.WriteAsync($"{string.Join(";", cells)}\r\n".AsMemory(), cancellationToken);
            if (++rowsWritten % 128 == 0)
                await writer.FlushAsync(cancellationToken);
        }

        await writer.FlushAsync(cancellationToken);
        return new EmptyResult();
    }

    private IQueryable<InventoryMovement> BuildMovementQuery(Guid companyId, string? machineId, int? itemNumber)
    {
        var query = _db.InventoryMovements.AsNoTracking().Where(movement =>
            movement.CompanyId == companyId &&
            _db.Machines.Any(machine => machine.Id == movement.MachineId && machine.CompanyId == companyId));
        if (!string.IsNullOrWhiteSpace(machineId)) query = query.Where(movement => movement.MachineId == machineId);
        if (itemNumber.HasValue) query = query.Where(movement => movement.ItemNumber == itemNumber.Value);
        return query;
    }

    private static string InventoryMovementLabel(string movementType) => movementType switch
    {
        "Sale" => "Venda",
        "SaleMappingMismatch" => "Venda sem baixa automática",
        "Restock" => "Reposição",
        "OpeningBalance" => "Saldo inicial",
        "Adjustment" => "Ajuste",
        _ => "Outro"
    };

    private static string CsvCell(string? value)
    {
        value ??= string.Empty;
        var firstValueCharacter = value.TrimStart();
        var safe = value.Length > 0 &&
                   ((value[0] is '\t' or '\r' or '\n') ||
                    (firstValueCharacter.Length > 0 && firstValueCharacter[0] is '=' or '+' or '-' or '@'))
            ? $"'{value}"
            : value;
        return $"\"{safe.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private async Task<IActionResult> ApplyMovementAsync(
        string machineId,
        int itemNumber,
        int quantityDelta,
        string movementType,
        string? reason,
        Guid? requestId,
        CancellationToken cancellationToken)
    {
        if (!TryGetEffectiveCompanyId(out var companyId))
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });
        if (itemNumber < 0 || itemNumber > 9999 || quantityDelta == 0 || quantityDelta is > 100000 or < -100000 ||
            string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 500 ||
            requestId == Guid.Empty || (movementType == "Restock" && quantityDelta < 0))
            return BadRequest(new { message = "Informe uma quantidade válida e o motivo da movimentação." });

        var userId = GetCurrentUserId();
        var normalizedReason = reason.Trim();
        // Use a chave existente do movimento, com escopo da empresa, sem exigir alteração de schema.
        var movementId = requestId.HasValue
            ? MovementIdForRequest(companyId, requestId.Value)
            : Guid.NewGuid();

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        if (!await MachineOperationLock.AcquireAsync(_db, machineId, companyId, cancellationToken))
            return NotFound(new { message = "Máquina não encontrada." });

        if (requestId.HasValue)
        {
            var existing = await _db.InventoryMovements.AsNoTracking()
                .SingleOrDefaultAsync(movement => movement.Id == movementId && movement.CompanyId == companyId, cancellationToken);
            if (existing is not null)
            {
                if (existing.MachineId != machineId || existing.ItemNumber != itemNumber ||
                    existing.MovementType != movementType || existing.QuantityDelta != quantityDelta ||
                    existing.Reason != normalizedReason || existing.UserId != userId)
                    return Conflict(new { code = "inventory_request_id_reused", message = "O identificador da movimentação já foi usado com outros dados." });

                return Ok(new { machineId, itemNumber, stock = existing.QuantityAfter, movementType });
            }
        }

        if (await HasActiveSessionAsync(machineId, companyId, cancellationToken))
            return Conflict(new { code = "active_vend_session", message = "Finalize a sessão MDB ativa antes de alterar o estoque." });

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
            Id = movementId,
            CompanyId = companyId,
            MachineId = machineId,
            ItemNumber = itemNumber,
            ProductId = slotState.ProductId,
            UserId = userId,
            MovementType = movementType,
            QuantityDelta = quantityDelta,
            QuantityAfter = slotState.CurrentStock,
            Reason = normalizedReason,
            CreatedAt = now
        });
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException error) when (requestId.HasValue && IsMovementIdConflict(error))
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _db.ChangeTracker.Clear();
            return Conflict(new { code = "inventory_request_id_reused", message = "O identificador da movimentação já foi usado com outros dados." });
        }
        await transaction.CommitAsync(cancellationToken);

        await _audit.RecordAsync(
            movementType == "Restock" ? "inventory.restocked" : "inventory.adjusted",
            "MachineProductSlot", $"{machineId}:{itemNumber}", companyId,
            new { quantityDelta, quantityAfter = slotState.CurrentStock, reason = reason.Trim() }, cancellationToken);
        return Ok(new { machineId, itemNumber, stock = slotState.CurrentStock, movementType });
    }

    private Task<bool> HasActiveSessionAsync(string machineId, Guid companyId, CancellationToken cancellationToken) =>
        _db.MachineSessions.AsNoTracking().AnyAsync(
            session => session.CompanyId == companyId && session.MachineId == machineId && session.ClosedAt == null,
            cancellationToken);

    private static bool IsMovementIdConflict(DbUpdateException error) => error.InnerException switch
    {
        PostgresException postgres => postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
                                      postgres.ConstraintName == "PK_InventoryMovements",
        SqliteException sqlite => sqlite.SqliteErrorCode == 19 &&
                                  sqlite.Message.Contains("InventoryMovements.Id", StringComparison.Ordinal),
        _ => false,
    };

    private static Guid MovementIdForRequest(Guid companyId, Guid requestId) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"inventory-movement:{companyId:D}:{requestId:D}"))[..16]);

    private Guid? GetCurrentUserId() => Guid.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId)
        ? userId
        : null;
}

public sealed record ConfigureMachineSlotRequest(string ProductId, int Capacity, int InitialStock, int LowStockThreshold);
public sealed record InventoryQuantityRequest(int Quantity, string Reason, Guid? RequestId = null);
public sealed record InventoryAdjustmentRequest(int QuantityDelta, string Reason, Guid? RequestId = null);
