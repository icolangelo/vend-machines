using System.Security.Claims;
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

public sealed class CatalogMutationAuditTests
{
    [Fact]
    public async Task LocationMutationsRecordAuditForEffectiveCompany()
    {
        await using var scenario = await CreateScenarioAsync();
        var companyId = Guid.NewGuid();
        scenario.Db.Companies.Add(new Company { Id = companyId, Name = "Empresa" });
        await scenario.Db.SaveChangesAsync();
        var audit = new RecordingAuditLog();
        var controller = CreateLocationsController(scenario.Db, audit, companyId);

        var created = await controller.Create(new LocationRequest { Name = "Centro" });
        var location = Assert.IsType<LocationDto>(Assert.IsType<CreatedAtActionResult>(created).Value);
        await controller.Update(location.Id, new LocationRequest { Name = "Centro atualizado" });
        await controller.Delete(location.Id);

        Assert.Equal(
            new[] { "location.created", "location.updated", "location.deleted" },
            audit.Entries.Select(entry => entry.Action));
        Assert.All(audit.Entries, entry => Assert.Equal(companyId, entry.CompanyId));
        Assert.All(audit.Entries, entry => Assert.Equal("Location", entry.ResourceType));
        Assert.All(audit.Entries, entry => Assert.Equal(location.Id.ToString(), entry.ResourceId));
    }

    [Fact]
    public async Task ProductAndProductTypeMutationsRecordAuditWithoutProductData()
    {
        await using var scenario = await CreateScenarioAsync();
        var companyId = Guid.NewGuid();
        scenario.Db.Companies.Add(new Company { Id = companyId, Name = "Empresa" });
        await scenario.Db.SaveChangesAsync();
        var audit = new RecordingAuditLog();
        var controller = CreateProductsController(scenario.Db, audit, companyId);

        var createdTypeResult = await controller.CreateProductType(new ProductType { Name = "Bebidas" });
        var productType = Assert.IsType<ProductType>(Assert.IsType<OkObjectResult>(createdTypeResult.Result).Value);
        await controller.UpdateProductType(productType.Id, new ProductType { Name = "Bebidas frias" });

        var createdProductResult = await controller.CreateProduct(new FullProduct
        {
            Code = "AGUA-1", Name = "Água", TypeId = productType.Id, Cost = 3.50m
        });
        var product = Assert.IsType<FullProduct>(Assert.IsType<OkObjectResult>(createdProductResult.Result).Value);
        await controller.UpdateProduct(product.Id, new FullProduct
        {
            Code = "AGUA-1", Name = "Água mineral", TypeId = productType.Id, Cost = 4m
        });
        await controller.DeleteProduct(product.Id, CancellationToken.None);

        Assert.Equal(
            new[]
            {
                "product_type.created", "product_type.updated",
                "product.created", "product.updated", "product.deleted"
            },
            audit.Entries.Select(entry => entry.Action));
        Assert.All(audit.Entries, entry => Assert.Equal(companyId, entry.CompanyId));
        Assert.Equal(new[] { "ProductType", "ProductType", "Product", "Product", "Product" },
            audit.Entries.Select(entry => entry.ResourceType));
        Assert.All(audit.Entries, entry => Assert.Null(entry.Metadata));
    }

    private static LocationsController CreateLocationsController(AppDbContext db, IAuditLogService audit, Guid companyId)
    {
        var controller = new LocationsController(db, new DatabaseDataService(db), audit);
        SetCompanyContext(controller, companyId);
        return controller;
    }

    private static ProductsController CreateProductsController(AppDbContext db, IAuditLogService audit, Guid companyId)
    {
        var controller = new ProductsController(new DatabaseDataService(db), db, audit);
        SetCompanyContext(controller, companyId);
        return controller;
    }

    private static void SetCompanyContext(ControllerBase controller, Guid companyId)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim("company_id", companyId.ToString()),
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())
            }, "Test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = context };
    }

    private static async Task<TestScenario> CreateScenarioAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return new TestScenario(connection, db);
    }

    private sealed class TestScenario(SqliteConnection connection, AppDbContext db) : IAsyncDisposable
    {
        public AppDbContext Db { get; } = db;

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class RecordingAuditLog : IAuditLogService
    {
        public List<AuditEntry> Entries { get; } = new();

        public Task RecordAsync(
            string action,
            string resourceType,
            string? resourceId = null,
            Guid? companyId = null,
            object? metadata = null,
            CancellationToken cancellationToken = default)
        {
            Entries.Add(new AuditEntry(action, resourceType, resourceId, companyId, metadata));
            return Task.CompletedTask;
        }
    }

    private sealed record AuditEntry(
        string Action,
        string ResourceType,
        string? ResourceId,
        Guid? CompanyId,
        object? Metadata);
}
