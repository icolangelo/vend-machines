using System.Security.Claims;
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

public sealed class PaymentsWebhookInboxPrivacyTests
{
    [Fact]
    public async Task WebhookInboxDoesNotReturnStoredExceptionMessages()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.WebhookInboxMessages.Add(new WebhookInboxMessage
        {
            EventKey = new string('a', 64),
            PaymentId = "payment-123",
            EventType = "payment",
            EventAction = "payment.updated",
            Format = "webhook_v1",
            AuthenticationMode = "hmac_verified",
            Status = "Failed",
            LastError = "SQL error contained sensitive-password and customer@example.test"
        });
        await db.SaveChangesAsync();

        var controller = new PaymentsController(db, null!, null!, null!, null!);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.Role, AppRoles.PlatformAdmin)
                }, "Test"))
            }
        };

        var response = await controller.GetWebhookInbox(cancellationToken: CancellationToken.None);
        var json = JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(response).Value);

        Assert.Contains("payment-123", json);
        Assert.Contains("Falha no processamento do webhook", json);
        Assert.DoesNotContain("sensitive-password", json);
        Assert.DoesNotContain("customer@example.test", json);
        Assert.DoesNotContain("SQL error contained", json);
    }
}
