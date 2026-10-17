using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using VendingMachines.Api.Controllers;
using VendingMachines.Api.Data;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class MercadoPagoWebhookControllerTests
{
    private const string WebhookSecret = "local-webhook-test-secret";
    private const string Body = "{\"type\":\"payment\",\"action\":\"payment.updated\",\"data\":{\"id\":\"123456789\"}}";

    [Fact]
    public async Task Webhook_WithConfiguredSecret_RejectsMissingAndInvalidSignature()
    {
        await using var scenario = await CreateScenarioAsync();

        var missingSignature = CreateController(scenario.Db, signature: null, requestId: "request-missing");
        var missingResult = Assert.IsType<UnauthorizedObjectResult>(await missingSignature.Controller.WebhookMercadoPago(ParseBody()));
        Assert.Contains("assinatura", missingResult.Value?.ToString(), StringComparison.OrdinalIgnoreCase);

        var invalidSignature = CreateController(scenario.Db, signature: "ts=1700000000,v1=invalid", requestId: "request-invalid");
        var invalidResult = Assert.IsType<UnauthorizedObjectResult>(await invalidSignature.Controller.WebhookMercadoPago(ParseBody()));
        Assert.Contains("inválida", invalidResult.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await scenario.Db.WebhookInboxMessages.ToListAsync());
    }

    [Fact]
    public async Task Webhook_AcceptsValidOldSignatureAndDeduplicatesRepeatedRequestId()
    {
        await using var scenario = await CreateScenarioAsync();
        var oldTimestamp = DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeSeconds().ToString();
        var requestId = "same-provider-request";
        var signature = CreateSignature("123456789", requestId, oldTimestamp);

        var firstController = CreateController(scenario.Db, signature, requestId);
        var firstResult = Assert.IsType<OkObjectResult>(await firstController.Controller.WebhookMercadoPago(ParseBody()));
        Assert.Contains("accepted", JsonSerializer.Serialize(firstResult.Value));

        var duplicateController = CreateController(scenario.Db, signature, requestId);
        var duplicateResult = Assert.IsType<OkObjectResult>(await duplicateController.Controller.WebhookMercadoPago(ParseBody()));
        Assert.Contains("duplicate", JsonSerializer.Serialize(duplicateResult.Value));

        var inboxMessage = await scenario.Db.WebhookInboxMessages.SingleAsync();
        Assert.Equal("hmac_verified", inboxMessage.AuthenticationMode);
        Assert.Equal("Pending", inboxMessage.Status);
        Assert.Equal("123456789", inboxMessage.PaymentId);
    }

    [Fact]
    public async Task Webhook_AcceptsLegacyUnsignedNotificationOnlyAsDurableTrigger()
    {
        await using var scenario = await CreateScenarioAsync();
        var controller = CreateController(scenario.Db, signature: null, requestId: "legacy-ipn-request");
        using var document = JsonDocument.Parse("{\"topic\":\"payment\",\"id\":\"123456789\"}");

        var result = Assert.IsType<OkObjectResult>(await controller.Controller.WebhookMercadoPago(document.RootElement.Clone()));

        Assert.Contains("accepted", JsonSerializer.Serialize(result.Value));
        var message = await scenario.Db.WebhookInboxMessages.SingleAsync();
        Assert.Equal("legacy_ipn", message.Format);
        Assert.Equal("legacy_unsigned", message.AuthenticationMode);
        Assert.Equal("Pending", message.Status);
    }

    [Fact]
    public async Task Webhook_AcknowledgesUnrelatedNotificationWithoutQueueingIt()
    {
        await using var scenario = await CreateScenarioAsync();
        var controller = CreateController(scenario.Db, signature: null, requestId: "unrelated-request");
        using var document = JsonDocument.Parse("{\"type\":\"merchant_order\",\"data\":{\"id\":\"987654321\"}}");

        var result = Assert.IsType<OkResult>(await controller.Controller.WebhookMercadoPago(document.RootElement.Clone()));

        Assert.Empty(await scenario.Db.WebhookInboxMessages.ToListAsync());
    }

    [Fact]
    public async Task Webhook_WithoutConfiguredSecret_PersistsNotificationAsUnconfiguredTrigger()
    {
        await using var scenario = await CreateScenarioAsync();
        var controller = CreateController(scenario.Db, signature: null, requestId: "unconfigured-request", configureSecret: false);

        var result = Assert.IsType<OkObjectResult>(await controller.Controller.WebhookMercadoPago(ParseBody()));

        Assert.Contains("accepted", JsonSerializer.Serialize(result.Value));
        var message = await scenario.Db.WebhookInboxMessages.SingleAsync();
        Assert.Equal("unconfigured", message.AuthenticationMode);
        Assert.Equal("Pending", message.Status);
    }

    [Fact]
    public async Task Webhook_ReturnsServiceUnavailableAndDoesNotAcknowledgeWhenPersistenceFails()
    {
        await using var scenario = await CreateScenarioAsync();
        await scenario.Db.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER fail_webhook_inbox_insert BEFORE INSERT ON WebhookInboxMessages BEGIN SELECT RAISE(ABORT, 'simulated persistence failure'); END;");
        var controller = CreateController(scenario.Db, signature: null, requestId: "persistence-failure-request", configureSecret: false);

        var result = Assert.IsType<ObjectResult>(await controller.Controller.WebhookMercadoPago(ParseBody()));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Empty(await scenario.Db.WebhookInboxMessages.ToListAsync());
    }

    [Fact]
    public async Task RetryWebhook_RequeuesOnlyFinalFailureAndResetsAttemptState()
    {
        await using var scenario = await CreateScenarioAsync();
        var failed = new VendingMachines.Api.Models.WebhookInboxMessage
        {
            Id = Guid.NewGuid(),
            EventKey = new string('d', 64),
            PaymentId = "123456789",
            EventType = "payment",
            Format = "webhook_v1",
            AuthenticationMode = "hmac_verified",
            Status = "Failed",
            Attempts = 12,
            LastError = "HttpRequestException",
            ProcessingStartedAt = DateTime.UtcNow.AddMinutes(-5),
            ProcessedAt = DateTime.UtcNow.AddMinutes(-5)
        };
        scenario.Db.WebhookInboxMessages.Add(failed);
        await scenario.Db.SaveChangesAsync();
        var controller = CreateController(scenario.Db, signature: null, requestId: "admin-retry").Controller;
        controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, "PlatformAdmin")], "Test"));

        var accepted = Assert.IsAssignableFrom<ObjectResult>(await controller.RetryWebhook(failed.Id));
        Assert.Equal(StatusCodes.Status202Accepted, accepted.StatusCode);
        Assert.Contains("Pending", JsonSerializer.Serialize(accepted.Value));

        var requeued = await scenario.Db.WebhookInboxMessages.AsNoTracking().SingleAsync();
        Assert.Equal("Pending", requeued.Status);
        Assert.Equal(0, requeued.Attempts);
        Assert.Null(requeued.ProcessingStartedAt);
        Assert.Null(requeued.ProcessedAt);
        Assert.Null(requeued.LastError);
        Assert.NotNull(requeued.NextAttemptAt);

        Assert.IsType<ConflictObjectResult>(await controller.RetryWebhook(failed.Id));
        Assert.IsType<NotFoundObjectResult>(await controller.RetryWebhook(Guid.NewGuid()));
    }

    private static string CreateSignature(string paymentId, string requestId, string timestamp)
    {
        var manifest = $"id:{paymentId};request-id:{requestId};ts:{timestamp};";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(WebhookSecret));
        var hash = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(manifest))).ToLowerInvariant();
        return $"ts={timestamp},v1={hash}";
    }

    private static JsonElement ParseBody()
    {
        using var document = JsonDocument.Parse(Body);
        return document.RootElement.Clone();
    }

    private static (PaymentsController Controller, DefaultHttpContext Context) CreateController(
        AppDbContext db,
        string? signature,
        string requestId,
        bool configureSecret = true)
    {
        var configurationValues = new Dictionary<string, string?>();
        if (configureSecret) configurationValues["MercadoPago:WebhookSecret"] = WebhookSecret;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configurationValues)
            .Build();
        var controller = new PaymentsController(
            db,
            sessionOrchestrator: null!,
            scopeFactory: null!,
            configuration,
            environment: null!);
        var context = new DefaultHttpContext();
        context.Request.Headers["x-request-id"] = requestId;
        if (signature is not null)
        {
            context.Request.Headers["x-signature"] = signature;
        }
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        return (controller, context);
    }

    private static async Task<TestScenario> CreateScenarioAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
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
}
