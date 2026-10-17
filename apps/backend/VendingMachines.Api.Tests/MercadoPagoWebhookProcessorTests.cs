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

public sealed class MercadoPagoWebhookProcessorTests
{
    [Theory]
    [InlineData("approved", 2.5, true, "accredited", "PartiallyRefunded", "Approved", 2.5)]
    [InlineData("refunded", 0, true, "accredited", "Refunded", "Refunded", 10)]
    [InlineData("charged_back", 0, false, "in_process", "Disputed", "Chargeback", 1)]
    [InlineData("charged_back", 0, true, "settled", "Chargeback", "Chargeback", 10)]
    [InlineData("in_mediation", 0, false, "", "Disputed", "ReconciliationRequired", 1)]
    [InlineData("approved", 0, false, "accredited", "PartiallyRefunded", "Approved", 1)]
    [InlineData("charged_back", 0, false, "reimbursed", "ReconciliationRequired", "ReconciliationRequired", 1)]
    public async Task ProcessNext_UpdatesSaleFinancialStateFromCanonicalProviderStatus(
        string providerStatus,
        decimal providerRefundedAmount,
        bool includeRefundedAmount,
        string statusDetail,
        string expectedSaleStatus,
        string expectedTransactionStatus,
        decimal expectedRefundedAmount)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var companyId = Guid.NewGuid();
        var machineId = $"machine-{Guid.NewGuid():N}";
        var sessionId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        var paymentId = "123456789";
        var company = new Company { Id = companyId, Name = "Empresa de teste", Cnpj = "00000000000000" };
        var machine = new Machine { Id = machineId, Name = "Máquina", SerialNumber = "SERIAL-1", CompanyId = companyId };
        var transaction = new PaymentTransaction
        {
            Id = transactionId,
            CompanyId = companyId,
            MachineId = machineId,
            Amount = 10m,
            ApplicationFee = 1m,
            Status = "Approved",
            MercadoPagoPaymentId = paymentId,
            SendTelemetryToMachine = false
        };
        var session = new MachineSession
        {
            Id = sessionId,
            CompanyId = companyId,
            MachineId = machineId,
            TransactionId = transactionId,
            State = MachineSessionStates.Completed
        };
        var sale = new SaleRecord
        {
            CompanyId = companyId,
            MachineId = machineId,
            SessionId = sessionId,
            TransactionId = transactionId,
            ItemNumber = 1,
            Quantity = 1,
            GrossAmount = 10m,
            RefundedAmount = 1m,
            ApplicationFee = 1m,
            NetAmount = 9m,
            FinancialStatus = "PartiallyRefunded"
        };
        var integration = new MercadoPagoIntegration
        {
            CompanyId = companyId,
            IsActive = true,
            AccessToken = EncryptionService.Encrypt("test-access-token"),
            RefreshToken = EncryptionService.Encrypt("test-refresh-token"),
            ClientSecret = EncryptionService.Encrypt("test-client-secret"),
            ClientId = "test-client",
            AccessTokenExpiresAt = DateTime.UtcNow.AddHours(1)
        };
        db.Companies.Add(company);
        db.Machines.Add(machine);
        db.PaymentTransactions.Add(transaction);
        db.MachineSessions.Add(session);
        db.SaleRecords.Add(sale);
        db.MercadoPagoIntegrations.Add(integration);
        db.WebhookInboxMessages.Add(new WebhookInboxMessage
        {
            EventKey = new string('a', 64),
            PaymentId = paymentId,
            EventType = "payment",
            EventAction = "payment.updated",
            Format = "webhook_v1",
            AuthenticationMode = "hmac_verified"
        });
        await db.SaveChangesAsync();

        var refundProperty = includeRefundedAmount
            ? $",\"transaction_amount_refunded\":{providerRefundedAmount.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            : string.Empty;
        var clientFactory = new JsonHttpClientFactory($"{{\"status\":\"{providerStatus}\",\"status_detail\":\"{statusDetail}\"{refundProperty}}}");
        var configuration = new ConfigurationBuilder().Build();
        var panelHub = new TelemetryPanelHub(NullLogger<TelemetryPanelHub>.Instance);
        var connections = new TelemetryConnectionManager(
            new UnusedScopeFactory(), panelHub, NullLogger<TelemetryConnectionManager>.Instance);
        var orchestrator = new SessionOrchestrator(
            db, connections, panelHub, clientFactory, configuration,
            new TestWebHostEnvironment(), NullLogger<SessionOrchestrator>.Instance);
        var processor = new MercadoPagoWebhookProcessor(
            db, orchestrator, configuration, clientFactory,
            NullLogger<MercadoPagoWebhookProcessor>.Instance);

        var didWork = await processor.ProcessNextAsync(CancellationToken.None);

        Assert.True(didWork);
        var updatedSale = await db.SaleRecords.AsNoTracking().SingleAsync(record => record.TransactionId == transactionId);
        var updatedTransaction = await db.PaymentTransactions.AsNoTracking().SingleAsync(item => item.Id == transactionId);
        var inbox = await db.WebhookInboxMessages.AsNoTracking().SingleAsync();
        Assert.Equal(expectedSaleStatus, updatedSale.FinancialStatus);
        Assert.Equal(expectedTransactionStatus, updatedTransaction.Status);
        Assert.Equal("Processed", inbox.Status);
        Assert.Equal(expectedRefundedAmount, updatedSale.RefundedAmount);
    }

    private sealed class JsonHttpClientFactory(string responseBody) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new JsonResponseHandler(responseBody));
    }

    private sealed class JsonResponseHandler(string responseBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
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
