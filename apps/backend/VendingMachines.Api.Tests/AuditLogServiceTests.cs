using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VendingMachines.Api.Data;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class AuditLogServiceTests
{
    [Fact]
    public async Task RecordPersistsActorScopeAndRequestContextWithoutSecrets()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var userId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim("company_id", companyId.ToString())
            }, "Test"))
        };
        httpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("192.0.2.10");
        httpContext.Request.Headers.UserAgent = "audit-test";

        var service = new AuditLogService(
            db,
            new HttpContextAccessor { HttpContext = httpContext },
            NullLogger<AuditLogService>.Instance);

        await service.RecordAsync(
            "machine.telemetry_credential_rotated",
            "Machine",
            "VM-001",
            metadata: new { reason = "operator_requested" });

        var entry = await db.AuditLogs.SingleAsync();
        Assert.Equal(userId, entry.ActorUserId);
        Assert.Equal(companyId, entry.CompanyId);
        Assert.Equal("192.0.2.10", entry.IpAddress);
        Assert.Equal("audit-test", entry.UserAgent);
        Assert.Contains("operator_requested", entry.MetadataJson);
        Assert.DoesNotContain("password", entry.MetadataJson ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }
}
