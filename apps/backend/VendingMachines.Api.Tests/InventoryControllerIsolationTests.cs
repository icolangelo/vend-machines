using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Controllers;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class InventoryControllerIsolationTests
{
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
