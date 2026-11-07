using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using VendingMachines.Api.Controllers;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class PaymentsReconciliationResolutionTests
{
    [Fact]
    public async Task ResolvePaymentReconciliation_ApprovedPayment_UpdatesSaleAndAuditsEvidenceAtomically()
    {
        await using var fixture = await CreateFixtureAsync();
        var controller = CreateController(fixture.Db, AppRoles.PlatformAdmin, fixture.ActorId);

        var result = await controller.ResolvePaymentReconciliation(fixture.Transaction.Id, CreateRequest(fixture));

        Assert.IsType<OkObjectResult>(result);
        var transaction = await fixture.Db.PaymentTransactions.AsNoTracking().SingleAsync();
        var sale = await fixture.Db.SaleRecords.AsNoTracking().SingleAsync();
        var audit = await fixture.Db.AuditLogs.AsNoTracking().SingleAsync();

        Assert.Equal("Approved", transaction.Status);
        Assert.Equal("Settled", sale.FinancialStatus);
        Assert.Equal(0m, sale.RefundedAmount);
        Assert.Equal("payments.reconciliation_resolved", audit.Action);
        Assert.Equal(fixture.ActorId, audit.ActorUserId);
        Assert.Equal(fixture.Company.Id, audit.CompanyId);
        Assert.Contains("ticket-123", audit.MetadataJson);
        Assert.Equal(MachineSessionStates.Completed,
            (await fixture.Db.MachineSessions.AsNoTracking().SingleAsync()).State);
    }

    [Fact]
    public async Task ResolvePaymentReconciliation_StaleProviderSnapshot_LeavesFinancialRecordsAndAuditUnchanged()
    {
        await using var fixture = await CreateFixtureAsync();
        var controller = CreateController(fixture.Db, AppRoles.PlatformAdmin, fixture.ActorId);
        var staleRequest = CreateRequest(fixture, expectedProviderStatusDetail: "partially_refunded");

        var result = await controller.ResolvePaymentReconciliation(fixture.Transaction.Id, staleRequest);

        Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal("ReconciliationRequired", (await fixture.Db.PaymentTransactions.AsNoTracking().SingleAsync()).Status);
        Assert.Equal("ReconciliationRequired", (await fixture.Db.SaleRecords.AsNoTracking().SingleAsync()).FinancialStatus);
        Assert.Empty(await fixture.Db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task ResolvePaymentReconciliation_RejectedPayment_RecordsUnpaidWithoutChangingMachineSession()
    {
        await using var fixture = await CreateFixtureAsync();
        fixture.Transaction.MercadoPagoStatus = "rejected";
        fixture.Transaction.MercadoPagoStatusDetail = "cc_rejected_bad_filled_other";
        fixture.Transaction.ReconciliationReason = "provider_state_conflict";
        await fixture.Db.SaveChangesAsync();
        var controller = CreateController(fixture.Db, AppRoles.PlatformAdmin, fixture.ActorId);

        var result = await controller.ResolvePaymentReconciliation(
            fixture.Transaction.Id,
            CreateRequest(fixture, resolution: "rejected"));

        Assert.IsType<OkObjectResult>(result);
        var transaction = await fixture.Db.PaymentTransactions.AsNoTracking().SingleAsync();
        var sale = await fixture.Db.SaleRecords.AsNoTracking().SingleAsync();
        Assert.Equal("Rejected", transaction.Status);
        Assert.Equal("Unpaid", sale.FinancialStatus);
        Assert.Equal(0m, sale.RefundedAmount);
        Assert.Equal(MachineSessionStates.Completed,
            (await fixture.Db.MachineSessions.AsNoTracking().SingleAsync()).State);
        Assert.Equal("payments.reconciliation_resolved", (await fixture.Db.AuditLogs.AsNoTracking().SingleAsync()).Action);
    }

    [Fact]
    public async Task ResolvePaymentReconciliation_CompanyAdmin_IsForbidden()
    {
        await using var fixture = await CreateFixtureAsync();
        var controller = CreateController(fixture.Db, AppRoles.Admin, fixture.ActorId);

        var result = await controller.ResolvePaymentReconciliation(fixture.Transaction.Id, CreateRequest(fixture));

        var forbidden = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        Assert.Equal("ReconciliationRequired", (await fixture.Db.PaymentTransactions.AsNoTracking().SingleAsync()).Status);
        Assert.Empty(await fixture.Db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task ResolvePaymentReconciliation_MockProviderPayment_IsRejected()
    {
        await using var fixture = await CreateFixtureAsync();
        fixture.Transaction.MercadoPagoPaymentId = "mock_payment_123";
        await fixture.Db.SaveChangesAsync();
        var controller = CreateController(fixture.Db, AppRoles.PlatformAdmin, fixture.ActorId);

        var result = await controller.ResolvePaymentReconciliation(fixture.Transaction.Id, CreateRequest(fixture));

        Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal("ReconciliationRequired", (await fixture.Db.PaymentTransactions.AsNoTracking().SingleAsync()).Status);
        Assert.Empty(await fixture.Db.AuditLogs.ToListAsync());
    }

    [Fact]
    public async Task ConcurrentPostgresResolutions_OnlyOneFinancialUpdateAndAuditAreCommitted()
    {
        var connectionString = Environment.GetEnvironmentVariable("VENDMACHINES_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("VENDMACHINES_REQUIRE_TEST_POSTGRES"), "true", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("O teste de resolução concorrente exige VENDMACHINES_TEST_POSTGRES no CI.");
            return;
        }

        var databaseName = $"reconcile_resolution_{Guid.NewGuid():N}";
        var adminConnectionString = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" }.ConnectionString;
        await using (var admin = new NpgsqlConnection(adminConnectionString))
        {
            await admin.OpenAsync();
            await using var createDatabase = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin);
            await createDatabase.ExecuteNonQueryAsync();
        }

        var scopedConnectionString = new NpgsqlConnectionStringBuilder(connectionString) { Database = databaseName }.ConnectionString;
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(scopedConnectionString).Options;
            var company = new Company { Id = Guid.NewGuid(), Name = "Empresa concorrente", Cnpj = "12345678000199" };
            var machine = new Machine { Id = $"machine-{Guid.NewGuid():N}", Name = "Máquina concorrente", SerialNumber = $"SERIAL-{Guid.NewGuid():N}", CompanyId = company.Id };
            var actorId = Guid.NewGuid();
            var transaction = new PaymentTransaction
            {
                Id = Guid.NewGuid(), CompanyId = company.Id, MachineId = machine.Id, Amount = 10m,
                Status = "ReconciliationRequired", MercadoPagoPaymentId = "123456789",
                MercadoPagoStatus = "approved", MercadoPagoStatusDetail = "accredited",
                ReconciliationReason = "contradictory_approved_after_cancel",
                LastReconciliationAt = new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc),
                SendTelemetryToMachine = false
            };
            var session = new MachineSession
            {
                Id = Guid.NewGuid(), CompanyId = company.Id, MachineId = machine.Id,
                TransactionId = transaction.Id, State = MachineSessionStates.Completed
            };
            var sale = new SaleRecord
            {
                CompanyId = company.Id, MachineId = machine.Id, SessionId = session.Id,
                TransactionId = transaction.Id, ItemNumber = 1, Quantity = 1,
                GrossAmount = 10m, ApplicationFee = 1m, NetAmount = 9m,
                FinancialStatus = "ReconciliationRequired"
            };

            await using (var setup = new AppDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                setup.AddRange(company, machine, transaction, session, sale);
                await setup.SaveChangesAsync();
            }

            await using var firstDb = new AppDbContext(options);
            await using var secondDb = new AppDbContext(options);
            var firstController = CreateController(firstDb, AppRoles.PlatformAdmin, actorId);
            var secondController = CreateController(secondDb, AppRoles.PlatformAdmin, actorId);
            var request = new PaymentReconciliationResolutionRequest
            {
                Resolution = "approved",
                EvidenceReference = "ticket-race-1",
                ExpectedProviderStatus = "approved",
                ExpectedProviderStatusDetail = "accredited",
                ExpectedReconciliationReason = "contradictory_approved_after_cancel",
                ExpectedLastReconciliationAt = transaction.LastReconciliationAt
            };

            var results = await Task.WhenAll(
                firstController.ResolvePaymentReconciliation(transaction.Id, request),
                secondController.ResolvePaymentReconciliation(transaction.Id, request));

            Assert.Single(results.OfType<OkObjectResult>());
            Assert.Single(results.OfType<ConflictObjectResult>());
            await using var verification = new AppDbContext(options);
            Assert.Equal("Approved", (await verification.PaymentTransactions.AsNoTracking().SingleAsync()).Status);
            Assert.Equal("Settled", (await verification.SaleRecords.AsNoTracking().SingleAsync()).FinancialStatus);
            Assert.Single(await verification.AuditLogs.AsNoTracking().ToListAsync());
        }
        finally
        {
            await using var admin = new NpgsqlConnection(adminConnectionString);
            await admin.OpenAsync();
            await using var dropDatabase = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin);
            await dropDatabase.ExecuteNonQueryAsync();
        }
    }

    private static PaymentReconciliationResolutionRequest CreateRequest(
        Fixture fixture,
        string? expectedProviderStatusDetail = null,
        string resolution = "approved") => new()
    {
        Resolution = resolution,
        EvidenceReference = "ticket-123",
        ExpectedProviderStatus = fixture.Transaction.MercadoPagoStatus!,
        ExpectedProviderStatusDetail = expectedProviderStatusDetail ?? fixture.Transaction.MercadoPagoStatusDetail,
        ExpectedReconciliationReason = fixture.Transaction.ReconciliationReason,
        ExpectedLastReconciliationAt = fixture.Transaction.LastReconciliationAt
    };

    private static PaymentsController CreateController(AppDbContext db, string role, Guid actorId)
    {
        var controller = new PaymentsController(db, null!, null!, null!, null!);
        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, actorId.ToString()),
            new Claim(ClaimTypes.Role, role)
        ], "test"));
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private static async Task<Fixture> CreateFixtureAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var company = new Company { Id = Guid.NewGuid(), Name = "Empresa teste", Cnpj = "12345678000199" };
        var machine = new Machine { Id = "machine-reconciliation", Name = "Máquina teste", SerialNumber = "SERIAL-RECON", CompanyId = company.Id };
        var transactionId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var reconciliationTime = new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
        var transaction = new PaymentTransaction
        {
            Id = transactionId,
            CompanyId = company.Id,
            MachineId = machine.Id,
            Amount = 10m,
            ApplicationFee = 1m,
            Status = "ReconciliationRequired",
            MercadoPagoPaymentId = "123456789",
            MercadoPagoStatus = "approved",
            MercadoPagoStatusDetail = "accredited",
            ReconciliationReason = "contradictory_approved_after_cancel",
            LastReconciliationAt = reconciliationTime,
            SendTelemetryToMachine = false
        };
        var session = new MachineSession
        {
            Id = sessionId,
            CompanyId = company.Id,
            MachineId = machine.Id,
            TransactionId = transactionId,
            State = MachineSessionStates.Completed
        };
        var sale = new SaleRecord
        {
            CompanyId = company.Id,
            MachineId = machine.Id,
            SessionId = sessionId,
            TransactionId = transactionId,
            ItemNumber = 1,
            Quantity = 1,
            GrossAmount = 10m,
            ApplicationFee = 1m,
            NetAmount = 9m,
            FinancialStatus = "ReconciliationRequired"
        };
        db.Companies.Add(company);
        db.Machines.Add(machine);
        db.PaymentTransactions.Add(transaction);
        db.MachineSessions.Add(session);
        db.SaleRecords.Add(sale);
        await db.SaveChangesAsync();

        return new Fixture(db, connection, company, transaction, Guid.NewGuid());
    }

    private sealed class Fixture(
        AppDbContext db,
        SqliteConnection connection,
        Company company,
        PaymentTransaction transaction,
        Guid actorId) : IAsyncDisposable
    {
        public AppDbContext Db { get; } = db;
        public Company Company { get; } = company;
        public PaymentTransaction Transaction { get; } = transaction;
        public Guid ActorId { get; } = actorId;

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
