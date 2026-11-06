using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Controllers;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class InventoryControllerIsolationTests
{
    [Fact]
    public async Task MovementRetryAfterCommittedResponseFailureIsIdempotentAndScopedToCompany()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var companyA = new Company { Id = Guid.NewGuid(), Name = "Empresa A", Cnpj = "11111111000111" };
        var companyB = new Company { Id = Guid.NewGuid(), Name = "Empresa B", Cnpj = "22222222000111" };
        db.Companies.AddRange(companyA, companyB);
        db.Machines.AddRange(
            new Machine { Id = "retry-machine-a", Name = "Máquina A", SerialNumber = "RA", CompanyId = companyA.Id },
            new Machine { Id = "retry-machine-b", Name = "Máquina B", SerialNumber = "RB", CompanyId = companyB.Id });
        db.Products.AddRange(
            new FullProduct { Id = "retry-product-a", Name = "Produto A", CompanyId = companyA.Id },
            new FullProduct { Id = "retry-product-b", Name = "Produto B", CompanyId = companyB.Id });
        db.MachineProductSlots.AddRange(
            new MachineProductSlot { CompanyId = companyA.Id, MachineId = "retry-machine-a", ItemNumber = 1, ProductId = "retry-product-a", Capacity = 20, CurrentStock = 2 },
            new MachineProductSlot { CompanyId = companyB.Id, MachineId = "retry-machine-b", ItemNumber = 1, ProductId = "retry-product-b", Capacity = 20, CurrentStock = 2 });
        await db.SaveChangesAsync();

        var requestId = Guid.NewGuid();
        var audit = new FailFirstAudit();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        var controllerA = ControllerFor(companyA.Id, userA);
        var controllerB = ControllerFor(companyB.Id, userB);
        var request = new InventoryQuantityRequest(3, "Reposição sintética", requestId);
        Assert.IsType<NotFoundResult>(await controllerA.GetMovementByRequest(requestId, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => controllerA.Restock("retry-machine-a", 1, request, CancellationToken.None));
        Assert.IsType<OkObjectResult>(await controllerA.Restock("retry-machine-a", 1, request, CancellationToken.None));
        var receipt = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await controllerA.GetMovementByRequest(requestId, CancellationToken.None)).Value);
        Assert.Equal("retry-machine-a", receipt.GetProperty("MachineId").GetString());
        Assert.Equal(3, receipt.GetProperty("QuantityDelta").GetInt32());
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(request.Reason))).ToLowerInvariant(), receipt.GetProperty("reasonHash").GetString());
        Assert.IsType<NotFoundResult>(await ControllerFor(companyA.Id, Guid.NewGuid()).GetMovementByRequest(requestId, CancellationToken.None));
        Assert.IsType<NotFoundResult>(await controllerB.GetMovementByRequest(requestId, CancellationToken.None));
        Assert.IsType<ConflictObjectResult>(await controllerA.Restock("retry-machine-a", 1, request with { Quantity = 4 }, CancellationToken.None));
        Assert.IsType<ConflictObjectResult>(await controllerA.AdjustStock("retry-machine-a", 1, new InventoryAdjustmentRequest(3, request.Reason, requestId), CancellationToken.None));
        Assert.IsType<ConflictObjectResult>(await ControllerFor(companyA.Id, Guid.NewGuid()).Restock("retry-machine-a", 1, request, CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(await controllerA.Restock("retry-machine-a", 1, request with { RequestId = Guid.Empty }, CancellationToken.None));

        Assert.IsType<OkObjectResult>(await controllerB.Restock("retry-machine-b", 1, request, CancellationToken.None));
        db.ChangeTracker.Clear();
        Assert.Equal(new[] { 5, 5 }, await db.MachineProductSlots.OrderBy(slot => slot.MachineId).Select(slot => slot.CurrentStock).ToArrayAsync());
        Assert.Equal(2, await db.InventoryMovements.CountAsync());

        var legacyRequest = new InventoryQuantityRequest(2, "Reposição legada");
        Assert.IsType<OkObjectResult>(await controllerA.Restock("retry-machine-a", 1, legacyRequest, CancellationToken.None));
        Assert.IsType<OkObjectResult>(await controllerA.Restock("retry-machine-a", 1, legacyRequest, CancellationToken.None));
        db.ChangeTracker.Clear();
        Assert.Equal(new[] { 9, 5 }, await db.MachineProductSlots.OrderBy(slot => slot.MachineId).Select(slot => slot.CurrentStock).ToArrayAsync());
        Assert.Equal(4, await db.InventoryMovements.CountAsync());

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER inventory_test_duplicate BEFORE INSERT ON "InventoryMovements"
            WHEN NEW."Reason" = 'Duplicata sintética'
            BEGIN
                INSERT INTO "InventoryMovements" ("Id", "CompanyId", "MachineId", "ItemNumber", "ProductId", "SaleRecordId", "UserId", "MovementType", "QuantityDelta", "QuantityAfter", "Reason", "CreatedAt")
                VALUES (NEW."Id", NEW."CompanyId", NEW."MachineId", NEW."ItemNumber", NEW."ProductId", NEW."SaleRecordId", NEW."UserId", NEW."MovementType", NEW."QuantityDelta", NEW."QuantityAfter", 'inserção interna', NEW."CreatedAt");
            END;
            """);
        Assert.IsType<ConflictObjectResult>(await controllerA.Restock("retry-machine-a", 1,
            new InventoryQuantityRequest(1, "Duplicata sintética", Guid.NewGuid()), CancellationToken.None));
        Assert.Equal(new[] { 9, 5 }, await db.MachineProductSlots.AsNoTracking().OrderBy(slot => slot.MachineId).Select(slot => slot.CurrentStock).ToArrayAsync());
        Assert.Equal(4, await db.InventoryMovements.CountAsync());

        await db.Database.ExecuteSqlRawAsync("""
            DROP TRIGGER inventory_test_duplicate;
            CREATE TRIGGER inventory_test_unrelated BEFORE INSERT ON "InventoryMovements"
            BEGIN SELECT RAISE(ABORT, 'falha sintética diferente'); END;
            """);
        await Assert.ThrowsAsync<DbUpdateException>(() => controllerA.Restock("retry-machine-a", 1,
            new InventoryQuantityRequest(1, "Outra falha", Guid.NewGuid()), CancellationToken.None));
        db.ChangeTracker.Clear();
        Assert.Equal(new[] { 9, 5 }, await db.MachineProductSlots.AsNoTracking().OrderBy(slot => slot.MachineId).Select(slot => slot.CurrentStock).ToArrayAsync());
        Assert.Equal(4, await db.InventoryMovements.CountAsync());

        InventoryController ControllerFor(Guid companyId, Guid? userId = null) => new(db, audit)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim("company_id", companyId.ToString()), new Claim(ClaimTypes.NameIdentifier, userId?.ToString() ?? "")], "test"))
                }
            }
        };
    }

    private sealed class FailFirstAudit : IAuditLogService
    {
        private bool _first = true;
        public Task RecordAsync(string action, string resourceType, string? resourceId = null, Guid? companyId = null,
            object? metadata = null, CancellationToken cancellationToken = default)
        {
            if (_first)
            {
                _first = false;
                throw new InvalidOperationException("Falha sintética após o commit");
            }
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task InventoryMovementCsvStreamsAllFilteredRowsWithCompanyIsolationAndFormulaProtection()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var companyA = new Company { Id = Guid.NewGuid(), Name = "Empresa A", Cnpj = "11111111000111" };
        var companyB = new Company { Id = Guid.NewGuid(), Name = "Empresa B", Cnpj = "22222222000122" };
        var machineA = new Machine { Id = "export-machine-a", Name = "Máquina A", SerialNumber = "EA", CompanyId = companyA.Id };
        var machineB = new Machine { Id = "export-machine-b", Name = "Máquina B", SerialNumber = "EB", CompanyId = companyB.Id };
        var productA = new FullProduct { Id = "export-product-a", Name = "=Produto A", CompanyId = companyA.Id };
        var productB = new FullProduct { Id = "export-product-b", Name = "Produto confidencial B", CompanyId = companyB.Id };
        db.Companies.AddRange(companyA, companyB);
        db.Machines.AddRange(machineA, machineB);
        db.Products.AddRange(productA, productB);
        db.MachineProductSlots.AddRange(
            new MachineProductSlot { CompanyId = companyA.Id, MachineId = machineA.Id, ItemNumber = 2, ProductId = productA.Id },
            new MachineProductSlot { CompanyId = companyB.Id, MachineId = machineB.Id, ItemNumber = 2, ProductId = productB.Id });
        for (var index = 0; index < 120; index++)
        {
            db.InventoryMovements.Add(new InventoryMovement
            {
                Id = Guid.Parse($"00000000-0000-0000-0000-{index + 1:000000000000}"),
                CompanyId = companyA.Id,
                MachineId = machineA.Id,
                ItemNumber = 2,
                ProductId = productA.Id,
                MovementType = "Adjustment",
                QuantityDelta = index == 0 ? -2 : 1,
                QuantityAfter = index,
                Reason = index == 0 ? "=1+1" : $"Movimentação {index}",
                CreatedAt = DateTime.UtcNow.AddMinutes(index)
            });
        }
        db.InventoryMovements.Add(new InventoryMovement
        {
            CompanyId = companyB.Id,
            MachineId = machineB.Id,
            ItemNumber = 2,
            ProductId = productB.Id,
            MovementType = "Restock",
            QuantityDelta = 99,
            QuantityAfter = 99,
            Reason = "Não exportar"
        });
        await db.SaveChangesAsync();

        await using var responseBody = new MemoryStream();
        var controller = new InventoryController(db, null!)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim("company_id", companyA.Id.ToString())], "test")),
                    Response = { Body = responseBody }
                }
            }
        };

        var result = await controller.ExportMovements(machineId: null, itemNumber: 2, CancellationToken.None);
        Assert.IsType<EmptyResult>(result);
        Assert.Equal("text/csv; charset=utf-8", controller.Response.ContentType);
        Assert.Equal("attachment; filename=movimentacoes-estoque.csv", controller.Response.Headers["Content-Disposition"]);
        Assert.Equal("no-store", controller.Response.Headers["Cache-Control"]);

        var csv = Encoding.UTF8.GetString(responseBody.ToArray()).TrimStart('\uFEFF');
        var rows = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(121, rows.Length);
        Assert.Contains("Data UTC;ID da máquina;Compartimento;Produto;Tipo;Variação;Saldo;Motivo", rows[0]);
        Assert.Contains("\"'-2\"", csv);
        Assert.Contains("'=Produto A", csv);
        Assert.Contains("'=1+1", csv);
        Assert.DoesNotContain("Máquina B", csv);
        Assert.DoesNotContain("Produto confidencial B", csv);
        Assert.DoesNotContain("Não exportar", csv);
    }

    [Fact]
    public async Task ForeignMachineInventoryRowsAreHiddenAndCannotBeMutated()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var companyA = new Company { Id = Guid.NewGuid(), Name = "Empresa A", Cnpj = "11111111000111" };
        var companyB = new Company { Id = Guid.NewGuid(), Name = "Empresa B", Cnpj = "22222222000122" };
        var machineB = new Machine { Id = "machine-b", Name = "Máquina B", SerialNumber = "B", CompanyId = companyB.Id };
        var productB = new FullProduct { Id = "product-b", Name = "Produto confidencial B", Code = "B", CompanyId = companyB.Id };
        db.Companies.AddRange(companyA, companyB);
        db.Machines.Add(machineB);
        db.Products.Add(productB);
        db.MachineProductSlots.Add(new MachineProductSlot
        {
            CompanyId = companyA.Id,
            MachineId = machineB.Id,
            ItemNumber = 1,
            ProductId = productB.Id,
            Capacity = 20,
            CurrentStock = 10,
            LowStockThreshold = 2
        });
        db.InventoryMovements.Add(new InventoryMovement
        {
            CompanyId = companyA.Id,
            MachineId = machineB.Id,
            ItemNumber = 1,
            ProductId = productB.Id,
            MovementType = "Restock",
            QuantityDelta = 10,
            QuantityAfter = 10,
            Reason = "Fixture inconsistente"
        });
        await db.SaveChangesAsync();

        var controller = new InventoryController(db, null!)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim("company_id", companyA.Id.ToString())], "test"))
                }
            }
        };

        var slots = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(
            await controller.GetSlots(cancellationToken: CancellationToken.None)).Value);
        Assert.Empty(slots.EnumerateArray());

        var movementsResponse = Assert.IsType<OkObjectResult>(
            await controller.GetMovements(cancellationToken: CancellationToken.None));
        var movements = JsonSerializer.SerializeToElement(movementsResponse.Value);
        Assert.Empty(movements.GetProperty("items").EnumerateArray());

        var restock = await controller.Restock(
            machineB.Id,
            1,
            new InventoryQuantityRequest(5, "Reposição de teste"),
            CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(restock);
        Assert.Equal(10, (await db.MachineProductSlots.SingleAsync()).CurrentStock);
        Assert.Single(await db.InventoryMovements.ToListAsync());
    }
}
