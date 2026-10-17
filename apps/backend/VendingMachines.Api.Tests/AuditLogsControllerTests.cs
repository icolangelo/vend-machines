using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Controllers;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class AuditLogsControllerTests
{
    [Fact]
    public async Task GetFiltersByCompanyAndActionAndPaginatesNewestFirst()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var companyA = Guid.NewGuid();
        var companyB = Guid.NewGuid();
        var oldest = new AuditLog
        {
            CompanyId = companyA, Action = "user.role_changed", ResourceType = "User",
            ResourceId = "oldest", CreatedAt = DateTime.UtcNow.AddMinutes(-3)
        };
        var newest = new AuditLog
        {
            CompanyId = companyA, Action = "user.role_changed", ResourceType = "User",
            ResourceId = "newest", CreatedAt = DateTime.UtcNow.AddMinutes(-1)
        };
        db.AuditLogs.AddRange(
            oldest,
            newest,
            new AuditLog
            {
                CompanyId = companyA, Action = "user.deactivated", ResourceType = "User",
                ResourceId = "other-action", CreatedAt = DateTime.UtcNow
            },
            new AuditLog
            {
                CompanyId = companyB, Action = "user.role_changed", ResourceType = "User",
                ResourceId = "other-company", CreatedAt = DateTime.UtcNow.AddMinutes(1)
            });
        await db.SaveChangesAsync();

        var controller = new AuditLogsController(db);
        var firstPage = Assert.IsType<OkObjectResult>(await controller.Get(
            companyA, " user.role_changed ", page: 1, pageSize: 1));
        Assert.Equal(newest.Id, Assert.Single(GetItems(firstPage.Value!)).Id);
        Assert.Equal(1, GetProperty<int>(firstPage.Value!, "page"));
        Assert.Equal(1, GetProperty<int>(firstPage.Value!, "pageSize"));
        Assert.Equal(2, GetProperty<int>(firstPage.Value!, "totalItems"));
        Assert.Equal(2, GetProperty<int>(firstPage.Value!, "totalPages"));

        var secondPage = Assert.IsType<OkObjectResult>(await controller.Get(
            companyA, "user.role_changed", page: 2, pageSize: 1));
        Assert.Equal(oldest.Id, Assert.Single(GetItems(secondPage.Value!)).Id);
    }

    [Fact]
    public async Task GetClampsInvalidPageAndPageSize()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var controller = new AuditLogsController(db);

        var result = Assert.IsType<OkObjectResult>(await controller.Get(page: 0, pageSize: 500));

        Assert.Equal(1, GetProperty<int>(result.Value!, "page"));
        Assert.Equal(200, GetProperty<int>(result.Value!, "pageSize"));
        Assert.Equal(0, GetProperty<int>(result.Value!, "totalItems"));
        Assert.Equal(0, GetProperty<int>(result.Value!, "totalPages"));
    }

    private static List<AuditLog> GetItems(object value) => GetProperty<List<AuditLog>>(value, "items");

    private static T GetProperty<T>(object value, string propertyName) =>
        (T)value.GetType().GetProperty(propertyName)!.GetValue(value)!;
}
