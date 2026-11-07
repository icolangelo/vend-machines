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

public sealed class MercadoPagoReconciliationTests
{
    [Fact]
    public async Task ReconcilePayments_ClaimsPendingAndTerminalStatesOncePerInterval_AndSkipsMockPayment()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var companyId = Guid.NewGuid();
        var company = new Company
        {
            Id = companyId,
            Name = "Empresa de teste",
            Cnpj = "00000000000000",
            CreatedByUserId = Guid.NewGuid()
        };
        var machine = new Machine
        {
            Id = $"machine-{Guid.NewGuid():N}",
            Name = "Máquina de teste",
            SerialNumber = $"SERIAL-{Guid.NewGuid():N}",
            NormalizedSerialNumber = $"SERIAL-{Guid.NewGuid():N}".ToUpperInvariant(),
            CompanyId = companyId
        };
        var pending = new PaymentTransaction
        {
            Id = Guid.NewGuid(),
            CompanyId = companyId,
            MachineId = machine.Id,
            Amount = 10m,
            Status = "Pending",
            MercadoPagoPaymentId = "123456789",
            CreatedAt = DateTime.UtcNow.AddMinutes(-10)
        };
        var simulated = new PaymentTransaction
        {
            Id = Guid.NewGuid(),
            CompanyId = companyId,
            MachineId = machine.Id,
            Amount = 10m,
            Status = "Pending",
            MercadoPagoPaymentId = "mock_123456789",
            CreatedAt = DateTime.UtcNow.AddMinutes(-10)
        };
        var approved = new PaymentTransaction
        {
            Id = Guid.NewGuid(), CompanyId = companyId, MachineId = machine.Id,
            Amount = 10m, Status = "Approved", MercadoPagoPaymentId = "approved-987",
            CreatedAt = DateTime.UtcNow.AddDays(-2)
        };
        var rejected = new PaymentTransaction
        {
            Id = Guid.NewGuid(), CompanyId = companyId, MachineId = machine.Id,
            Amount = 10m, Status = "Rejected", MercadoPagoPaymentId = "rejected-654",
            CreatedAt = DateTime.UtcNow.AddDays(-2)
        };
        var reconciliationRequired = new PaymentTransaction
        {
            Id = Guid.NewGuid(), CompanyId = companyId, MachineId = machine.Id,
            Amount = 10m, Status = "ReconciliationRequired", MercadoPagoPaymentId = "reconciliation-321",
            CreatedAt = DateTime.UtcNow.AddDays(-2)
        };
        db.Companies.Add(company);
        db.Machines.Add(machine);
        db.PaymentTransactions.AddRange(pending, simulated, approved, rejected, reconciliationRequired);
        await db.SaveChangesAsync();

        var panelHub = new TelemetryPanelHub(NullLogger<TelemetryPanelHub>.Instance);
        var connections = new TelemetryConnectionManager(
            new UnusedScopeFactory(), panelHub, NullLogger<TelemetryConnectionManager>.Instance);
        var httpClientFactory = new NoRequestHttpClientFactory();
        var orchestrator = new SessionOrchestrator(
            db,
            connections,
            panelHub,
            httpClientFactory,
            new ConfigurationBuilder().Build(),
            new TestWebHostEnvironment(),
            NullLogger<SessionOrchestrator>.Instance);
        var processor = new MercadoPagoWebhookProcessor(
            db,
            orchestrator,
            new ConfigurationBuilder().Build(),
            httpClientFactory,
            NullLogger<MercadoPagoWebhookProcessor>.Instance);

        await processor.ReconcilePendingPaymentsAsync(CancellationToken.None);
        var firstClaim = await db.PaymentTransactions.AsNoTracking()
            .SingleAsync(transaction => transaction.Id == pending.Id);
        var approvedFirstClaim = await db.PaymentTransactions.AsNoTracking()
            .SingleAsync(transaction => transaction.Id == approved.Id);
        var rejectedFirstClaim = await db.PaymentTransactions.AsNoTracking()
            .SingleAsync(transaction => transaction.Id == rejected.Id);
        var reconciliationFirstClaim = await db.PaymentTransactions.AsNoTracking()
            .SingleAsync(transaction => transaction.Id == reconciliationRequired.Id);
        Assert.NotNull(firstClaim.LastReconciliationAt);

        await processor.ReconcilePendingPaymentsAsync(CancellationToken.None);
        var secondClaim = await db.PaymentTransactions.AsNoTracking()
            .SingleAsync(transaction => transaction.Id == pending.Id);
        var skippedSimulation = await db.PaymentTransactions.AsNoTracking()
            .SingleAsync(transaction => transaction.Id == simulated.Id);
        var approvedSecondClaim = await db.PaymentTransactions.AsNoTracking()
            .SingleAsync(transaction => transaction.Id == approved.Id);
        var rejectedSecondClaim = await db.PaymentTransactions.AsNoTracking()
            .SingleAsync(transaction => transaction.Id == rejected.Id);
        var reconciliationSecondClaim = await db.PaymentTransactions.AsNoTracking()
            .SingleAsync(transaction => transaction.Id == reconciliationRequired.Id);

        Assert.Equal(firstClaim.LastReconciliationAt, secondClaim.LastReconciliationAt);
        Assert.Null(skippedSimulation.LastReconciliationAt);
        Assert.Equal("Pending", secondClaim.Status);
        Assert.NotNull(approvedFirstClaim.LastReconciliationAt);
        Assert.Equal(approvedFirstClaim.LastReconciliationAt, approvedSecondClaim.LastReconciliationAt);
        Assert.NotNull(rejectedFirstClaim.LastReconciliationAt);
        Assert.Equal(rejectedFirstClaim.LastReconciliationAt, rejectedSecondClaim.LastReconciliationAt);
        Assert.NotNull(reconciliationFirstClaim.LastReconciliationAt);
        Assert.Equal(reconciliationFirstClaim.LastReconciliationAt, reconciliationSecondClaim.LastReconciliationAt);
    }

    private sealed class UnusedScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("Escopo não utilizado.");
    }

    private sealed class NoRequestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
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
