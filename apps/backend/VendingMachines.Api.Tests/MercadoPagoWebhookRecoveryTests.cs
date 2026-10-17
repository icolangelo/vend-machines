using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class MercadoPagoWebhookRecoveryTests
{
    [Fact]
    public async Task Processor_ReclaimsStaleProcessingAfterRestartAndRetriesTransientProviderFailure()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using (var seedDb = new AppDbContext(options))
        {
            await seedDb.Database.EnsureCreatedAsync();
            var company = new Company { Id = Guid.NewGuid(), Name = "Empresa de recuperação", Cnpj = "00000000000000" };
            var machine = new Machine
            {
                Id = $"machine-{Guid.NewGuid():N}", Name = "Máquina de recuperação", SerialNumber = "RECOVERY-1", CompanyId = company.Id
            };
            var transaction = new PaymentTransaction
            {
                Id = Guid.NewGuid(), CompanyId = company.Id, MachineId = machine.Id, Amount = 10m,
                Status = "Pending", MercadoPagoPaymentId = "987654321", SendTelemetryToMachine = false
            };
            seedDb.Companies.Add(company);
            seedDb.Machines.Add(machine);
            seedDb.PaymentTransactions.Add(transaction);
            seedDb.MercadoPagoIntegrations.Add(new MercadoPagoIntegration
            {
                CompanyId = company.Id,
                IsActive = true,
                AccessToken = "test-access-token",
                RefreshToken = "test-refresh-token",
                ClientSecret = "test-client-secret",
                ClientId = "test-client",
                AccessTokenExpiresAt = DateTime.UtcNow.AddHours(1)
            });
            seedDb.WebhookInboxMessages.Add(new WebhookInboxMessage
            {
                Id = Guid.NewGuid(),
                EventKey = new string('b', 64),
                PaymentId = transaction.MercadoPagoPaymentId!,
                EventType = "payment",
                EventAction = "payment.updated",
                Format = "webhook_v1",
                AuthenticationMode = "hmac_verified",
                Status = "Processing",
                Attempts = 1,
                ProcessingStartedAt = DateTime.UtcNow.AddMinutes(-3)
            });
            await seedDb.SaveChangesAsync();
        }

        await using var processingDb = new AppDbContext(options);
        var failedProcessor = CreateProcessor(processingDb, HttpStatusCode.BadGateway, "temporarily unavailable");

        Assert.True(await failedProcessor.ProcessNextAsync(CancellationToken.None));
        var retried = await processingDb.WebhookInboxMessages.AsNoTracking().SingleAsync();
        Assert.Equal("Retry", retried.Status);
        Assert.Equal(2, retried.Attempts);
        Assert.Null(retried.ProcessingStartedAt);
        Assert.NotNull(retried.NextAttemptAt);

        await processingDb.WebhookInboxMessages
            .Where(message => message.Id == retried.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(message => message.NextAttemptAt, (DateTime?)null));
        var successfulProcessor = CreateProcessor(
            processingDb,
            HttpStatusCode.OK,
            "{\"status\":\"approved\",\"status_detail\":\"accredited\",\"transaction_amount_refunded\":0}");

        Assert.True(await successfulProcessor.ProcessNextAsync(CancellationToken.None));
        Assert.False(await successfulProcessor.ProcessNextAsync(CancellationToken.None));

        var processed = await processingDb.WebhookInboxMessages.AsNoTracking().SingleAsync();
        var payment = await processingDb.PaymentTransactions.AsNoTracking().SingleAsync();
        Assert.Equal("Processed", processed.Status);
        Assert.Equal(3, processed.Attempts);
        Assert.Null(processed.NextAttemptAt);
        Assert.Equal("Approved", payment.Status);
        Assert.Equal("approved", payment.MercadoPagoStatus);
    }

    [Fact]
    public async Task Processor_MovesMessageToFinalFailureAtMaximumAttemptAndStopsAutomaticRetry()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using (var seedDb = new AppDbContext(options))
        {
            await seedDb.Database.EnsureCreatedAsync();
            var company = new Company { Id = Guid.NewGuid(), Name = "Empresa de limite", Cnpj = "00000000000000" };
            var machine = new Machine
            {
                Id = $"machine-{Guid.NewGuid():N}", Name = "Máquina de limite", SerialNumber = "LIMIT-1", CompanyId = company.Id
            };
            var transaction = new PaymentTransaction
            {
                Id = Guid.NewGuid(), CompanyId = company.Id, MachineId = machine.Id, Amount = 10m,
                Status = "Pending", MercadoPagoPaymentId = "987654320", SendTelemetryToMachine = false
            };
            seedDb.Companies.Add(company);
            seedDb.Machines.Add(machine);
            seedDb.PaymentTransactions.Add(transaction);
            seedDb.MercadoPagoIntegrations.Add(new MercadoPagoIntegration
            {
                CompanyId = company.Id,
                IsActive = true,
                AccessToken = "test-access-token",
                RefreshToken = "test-refresh-token",
                ClientSecret = "test-client-secret",
                ClientId = "test-client",
                AccessTokenExpiresAt = DateTime.UtcNow.AddHours(1)
            });
            seedDb.WebhookInboxMessages.Add(new WebhookInboxMessage
            {
                Id = Guid.NewGuid(),
                EventKey = new string('e', 64),
                PaymentId = transaction.MercadoPagoPaymentId!,
                EventType = "payment",
                EventAction = "payment.updated",
                Format = "webhook_v1",
                AuthenticationMode = "hmac_verified",
                Status = "Processing",
                Attempts = 11,
                ProcessingStartedAt = DateTime.UtcNow.AddMinutes(-3)
            });
            await seedDb.SaveChangesAsync();
        }

        await using var processingDb = new AppDbContext(options);
        var processor = CreateProcessor(processingDb, HttpStatusCode.BadGateway, "provider unavailable");

        Assert.True(await processor.ProcessNextAsync(CancellationToken.None));
        var failed = await processingDb.WebhookInboxMessages.AsNoTracking().SingleAsync();
        Assert.Equal("Failed", failed.Status);
        Assert.Equal(12, failed.Attempts);
        Assert.Null(failed.ProcessingStartedAt);
        Assert.Null(failed.NextAttemptAt);
        Assert.Equal(nameof(HttpRequestException), failed.LastError);
        Assert.DoesNotContain("temporarily unavailable", failed.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.False(await processor.ProcessNextAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ProcessorRetriesWebhookThatArrivesBeforePaymentIsStored()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var paymentId = "payment-created-after-webhook";
        var inbox = new WebhookInboxMessage
        {
            EventKey = new string('f', 64),
            PaymentId = paymentId,
            EventType = "payment",
            EventAction = "payment.updated",
            Format = "webhook_v1",
            AuthenticationMode = "hmac_verified"
        };
        db.WebhookInboxMessages.Add(inbox);
        await db.SaveChangesAsync();

        var processor = CreateProcessor(
            db,
            HttpStatusCode.OK,
            "{\"status\":\"approved\",\"status_detail\":\"accredited\",\"transaction_amount_refunded\":0}");
        Assert.True(await processor.ProcessNextAsync(CancellationToken.None));

        var retry = await db.WebhookInboxMessages.AsNoTracking().SingleAsync();
        Assert.Equal("Retry", retry.Status);
        Assert.Equal(1, retry.Attempts);
        Assert.NotNull(retry.NextAttemptAt);
        Assert.Equal(nameof(InvalidOperationException), retry.LastError);

        var company = new Company { Id = Guid.NewGuid(), Name = "Empresa criada após webhook", Cnpj = "00000000000000" };
        var machine = new Machine
        {
            Id = "machine-created-after-webhook", Name = "Máquina", SerialNumber = "AFTER-WEBHOOK-1",
            CompanyId = company.Id
        };
        db.Companies.Add(company);
        db.Machines.Add(machine);
        db.PaymentTransactions.Add(new PaymentTransaction
        {
            Id = Guid.NewGuid(),
            CompanyId = company.Id,
            MachineId = machine.Id,
            Amount = 12m,
            Status = "Pending",
            MercadoPagoPaymentId = paymentId,
            SendTelemetryToMachine = false
        });
        db.MercadoPagoIntegrations.Add(new MercadoPagoIntegration
        {
            CompanyId = company.Id,
            IsActive = true,
            AccessToken = EncryptionService.Encrypt("test-access-token"),
            RefreshToken = EncryptionService.Encrypt("test-refresh-token"),
            ClientSecret = EncryptionService.Encrypt("test-client-secret"),
            ClientId = "test-client",
            AccessTokenExpiresAt = DateTime.UtcNow.AddHours(1)
        });
        await db.SaveChangesAsync();
        await db.WebhookInboxMessages.Where(message => message.Id == inbox.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(message => message.NextAttemptAt, (DateTime?)null));

        Assert.True(await processor.ProcessNextAsync(CancellationToken.None));

        var processed = await db.WebhookInboxMessages.AsNoTracking().SingleAsync();
        var payment = await db.PaymentTransactions.AsNoTracking().SingleAsync();
        Assert.Equal("Processed", processed.Status);
        Assert.Equal(2, processed.Attempts);
        Assert.Null(processed.NextAttemptAt);
        Assert.Equal("Approved", payment.Status);
        Assert.Equal("approved", payment.MercadoPagoStatus);
    }

    private static MercadoPagoWebhookProcessor CreateProcessor(AppDbContext db, HttpStatusCode statusCode, string responseBody)
    {
        var configuration = new ConfigurationBuilder().Build();
        var panelHub = new TelemetryPanelHub(NullLogger<TelemetryPanelHub>.Instance);
        var connections = new TelemetryConnectionManager(
            new UnusedScopeFactory(), panelHub, NullLogger<TelemetryConnectionManager>.Instance);
        var orchestrator = new SessionOrchestrator(
            db, connections, panelHub, new FixedHttpClientFactory(statusCode, responseBody), configuration,
            new TestWebHostEnvironment(), NullLogger<SessionOrchestrator>.Instance);
        return new MercadoPagoWebhookProcessor(
            db, orchestrator, configuration, new FixedHttpClientFactory(statusCode, responseBody),
            NullLogger<MercadoPagoWebhookProcessor>.Instance);
    }

    private sealed class FixedHttpClientFactory(HttpStatusCode statusCode, string responseBody) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new FixedResponseHandler(statusCode, responseBody));
    }

    private sealed class FixedResponseHandler(HttpStatusCode statusCode, string responseBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            });
    }

    private sealed class UnusedScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("Escopo não utilizado.");
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "VendingMachines.Api.Tests";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
