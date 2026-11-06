using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using VendingMachines.Api.Controllers;
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
            metadata: new
            {
                reason = "operator_requested",
                accessToken = "sensitive-access-token",
                nested = new
                {
                    client_secret = "sensitive-client-secret",
                    headers = new[] { new { Authorization = "Bearer sensitive-header-token" } }
                }
            });

        var entry = await db.AuditLogs.SingleAsync();
        Assert.Equal(userId, entry.ActorUserId);
        Assert.Equal(companyId, entry.CompanyId);
        Assert.Equal("192.0.2.10", entry.IpAddress);
        Assert.Equal("audit-test", entry.UserAgent);
        Assert.Contains("operator_requested", entry.MetadataJson);
        Assert.DoesNotContain("password", entry.MetadataJson ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sensitive-access-token", entry.MetadataJson ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-client-secret", entry.MetadataJson ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-header-token", entry.MetadataJson ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(3, CountOccurrences(entry.MetadataJson ?? string.Empty, "[REDACTED]"));
    }

    [Fact]
    public async Task AuditLogEndpointRedactsSecretsFromHistoricalMetadata()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.AuditLogs.Add(new VendingMachines.Api.Models.AuditLog
        {
            Action = "test.action",
            ResourceType = "Test",
            MetadataJson = "{\"reason\":\"safe\",\"refreshToken\":\"historical-sensitive-token\"}"
        });
        await db.SaveChangesAsync();

        var controller = new AuditLogsController(db);
        var response = Assert.IsType<OkObjectResult>(await controller.Get());
        var payload = JsonSerializer.SerializeToElement(response.Value);
        var item = payload.GetProperty("items")[0];
        var serializedItem = item.ToString();

        Assert.Contains("safe", serializedItem, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", serializedItem, StringComparison.Ordinal);
        Assert.DoesNotContain("historical-sensitive-token", serializedItem, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactorOmitsMalformedOrScalarHistoricalMetadata()
    {
        Assert.Equal("\"[METADATA_OMITTED]\"", AuditMetadataRedactor.RedactJson("not-json-sensitive-value"));
        Assert.Equal("\"[METADATA_OMITTED]\"", AuditMetadataRedactor.RedactJson("\"scalar-sensitive-value\""));
    }

    private static int CountOccurrences(string value, string fragment) =>
        (value.Length - value.Replace(fragment, string.Empty, StringComparison.Ordinal).Length) / fragment.Length;
}
