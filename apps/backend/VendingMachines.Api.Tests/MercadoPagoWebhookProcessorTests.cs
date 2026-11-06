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
using Npgsql;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class MercadoPagoWebhookProcessorTests
{
    [Theory]
    [InlineData("approved", 2.5, true, "accredited", "PartiallyRefunded", "Approved", 2.5, 10)]
    [InlineData("approved", 9.5, true, "accredited", "ReconciliationRequired", "ReconciliationRequired", 1, 9)]
    [InlineData("approved", -0.01, true, "accredited", "ReconciliationRequired", "ReconciliationRequired", 1, 10)]
    [InlineData("approved", 10, true, "accredited", "ReconciliationRequired", "ReconciliationRequired", 1, 10)]
    [InlineData("approved", 10.01, true, "accredited", "ReconciliationRequired", "ReconciliationRequired", 1, 10)]
    [InlineData("refunded", 0, true, "accredited", "Refunded", "Refunded", 10, 10)]
    [InlineData("charged_back", 0, false, "in_process", "Disputed", "Chargeback", 1, 10)]
    [InlineData("charged_back", 0, true, "settled", "Chargeback", "Chargeback", 10, 10)]
    [InlineData("in_mediation", 0, false, "", "Disputed", "ReconciliationRequired", 1, 10)]
    [InlineData("approved", 0, false, "accredited", "PartiallyRefunded", "Approved", 1, 10)]
    [InlineData("charged_back", 0, false, "reimbursed", "ReconciliationRequired", "ReconciliationRequired", 1, 10)]
    [InlineData("in_process", 0, false, "pending_contingency", "ReconciliationRequired", "ReconciliationRequired", 1, 10)]
    [InlineData("rejected", 0, false, "cc_rejected_bad_filled_other", "ReconciliationRequired", "ReconciliationRequired", 1, 10)]
    [InlineData("cancelled", 0, false, "by_collector", "ReconciliationRequired", "ReconciliationRequired", 1, 10)]
    public async Task ProcessNext_UpdatesSaleFinancialStateFromCanonicalProviderStatus(
        string providerStatus,
        decimal providerRefundedAmount,
        bool includeRefundedAmount,
        string statusDetail,
        string expectedSaleStatus,
        string expectedTransactionStatus,
        decimal expectedRefundedAmount,
        decimal saleGrossAmount)
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
            GrossAmount = saleGrossAmount,
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

        var finalExpectedSaleStatus = expectedSaleStatus;
        var finalExpectedTransactionStatus = expectedTransactionStatus;
        var finalExpectedRefundedAmount = expectedRefundedAmount;
        if (providerStatus is "rejected" or "cancelled")
        {
            // Represents the PlatformAdmin's audited decision after checking the canonical provider state.
            var manuallyResolvedTransaction = await db.PaymentTransactions.SingleAsync(item => item.Id == transactionId);
            manuallyResolvedTransaction.Status = providerStatus == "rejected" ? "Rejected" : "Cancelled";
            manuallyResolvedTransaction.ReconciliationReason = null;
            var manuallyResolvedSale = await db.SaleRecords.SingleAsync(record => record.TransactionId == transactionId);
            manuallyResolvedSale.FinancialStatus = "Unpaid";
            manuallyResolvedSale.RefundedAmount = 0m;
            await db.SaveChangesAsync();
            finalExpectedSaleStatus = "Unpaid";
            finalExpectedTransactionStatus = manuallyResolvedTransaction.Status;
            finalExpectedRefundedAmount = 0m;
        }

        db.WebhookInboxMessages.Add(new WebhookInboxMessage
        {
            EventKey = new string('b', 64),
            PaymentId = paymentId,
            EventType = "payment",
            EventAction = "payment.updated",
            Format = "webhook_v1",
            AuthenticationMode = "hmac_verified"
        });
        await db.SaveChangesAsync();

        Assert.True(await processor.ProcessNextAsync(CancellationToken.None));
        updatedSale = await db.SaleRecords.AsNoTracking().SingleAsync(record => record.TransactionId == transactionId);
        updatedTransaction = await db.PaymentTransactions.AsNoTracking().SingleAsync(item => item.Id == transactionId);
        Assert.Equal(finalExpectedSaleStatus, updatedSale.FinancialStatus);
        Assert.Equal(finalExpectedTransactionStatus, updatedTransaction.Status);
        Assert.Equal(finalExpectedRefundedAmount, updatedSale.RefundedAmount);
    }

    [Theory]
    [InlineData("Refunded", "", "Refunded", "refunded", "accredited", "provider_approval_after_financial_reversal", 10)]
    [InlineData("Chargeback", "", "Chargeback", "charged_back", "settled", "provider_approval_after_financial_reversal", 10)]
    [InlineData("ReconciliationRequired", "provider_regression_after_chargeback", "ReconciliationRequired", "charged_back", "settled", "provider_regression_after_chargeback", 10)]
    [InlineData("Rejected", "", "Unpaid", "rejected", "cc_rejected_bad_filled_other", "provider_approval_after_rejected_or_cancelled", 0)]
    [InlineData("Cancelled", "", "Unpaid", "cancelled", "by_collector", "provider_approval_after_rejected_or_cancelled", 0)]
    public async Task ProcessNext_ConflictingApprovalAfterReversal_StaysUnresolvedAndDoesNotDispatchToMachine(
        string previousTransactionStatus,
        string previousReconciliationReason,
        string previousSaleStatus,
        string previousProviderStatus,
        string previousProviderDetail,
        string expectedReconciliationReason,
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
        const string paymentId = "987654321";
        var company = new Company { Id = companyId, Name = "Empresa de teste", Cnpj = "00000000000000" };
        var machine = new Machine { Id = machineId, Name = "Máquina", SerialNumber = "SERIAL-CONFLICT", CompanyId = companyId };
        var transaction = new PaymentTransaction
        {
            Id = transactionId,
            CompanyId = companyId,
            MachineId = machineId,
            Amount = 10m,
            ApplicationFee = 1m,
            Status = previousTransactionStatus,
            MercadoPagoPaymentId = paymentId,
            MercadoPagoStatus = previousProviderStatus,
            MercadoPagoStatusDetail = previousProviderDetail,
            ReconciliationReason = string.IsNullOrEmpty(previousReconciliationReason) ? null : previousReconciliationReason,
            SendTelemetryToMachine = true
        };
        var session = new MachineSession
        {
            Id = sessionId,
            CompanyId = companyId,
            MachineId = machineId,
            TransactionId = transactionId,
            State = MachineSessionStates.PaymentPending,
            PaymentDeadlineAt = DateTime.UtcNow.AddMinutes(5)
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
            RefundedAmount = expectedRefundedAmount,
            ApplicationFee = 1m,
            NetAmount = 9m,
            FinancialStatus = previousSaleStatus
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
        db.WebhookInboxMessages.Add(CreateInboxMessage(paymentId, 'c'));
        await db.SaveChangesAsync();

        var clientFactory = new JsonHttpClientFactory("{\"status\":\"approved\",\"status_detail\":\"accredited\"}");
        var configuration = new ConfigurationBuilder().Build();
        var panelHub = new TelemetryPanelHub(NullLogger<TelemetryPanelHub>.Instance);
        var connections = new TelemetryConnectionManager(new UnusedScopeFactory(), panelHub, NullLogger<TelemetryConnectionManager>.Instance);
        var orchestrator = new SessionOrchestrator(
            db, connections, panelHub, clientFactory, configuration,
            new TestWebHostEnvironment(), NullLogger<SessionOrchestrator>.Instance);
        var processor = new MercadoPagoWebhookProcessor(db, orchestrator, configuration, clientFactory,
            NullLogger<MercadoPagoWebhookProcessor>.Instance);

        Assert.True(await processor.ProcessNextAsync(CancellationToken.None));
        await AssertConflictingApprovalStateAsync();

        db.WebhookInboxMessages.Add(CreateInboxMessage(paymentId, 'd'));
        await db.SaveChangesAsync();
        Assert.True(await processor.ProcessNextAsync(CancellationToken.None));
        await AssertConflictingApprovalStateAsync();

        async Task AssertConflictingApprovalStateAsync()
        {
            var currentTransaction = await db.PaymentTransactions.AsNoTracking().SingleAsync(item => item.Id == transactionId);
            var currentSale = await db.SaleRecords.AsNoTracking().SingleAsync(item => item.TransactionId == transactionId);
            var currentSession = await db.MachineSessions.AsNoTracking().SingleAsync(item => item.Id == sessionId);
            Assert.Equal("ReconciliationRequired", currentTransaction.Status);
            Assert.Equal(expectedReconciliationReason, currentTransaction.ReconciliationReason);
            Assert.Equal("ReconciliationRequired", currentSale.FinancialStatus);
            Assert.Equal(expectedRefundedAmount, currentSale.RefundedAmount);
            Assert.Equal(MachineSessionStates.PaymentPending, currentSession.State);
            Assert.Empty(await db.TelemetryCommands.AsNoTracking().ToListAsync());
            Assert.Empty(await db.OutboxMessages.AsNoTracking().ToListAsync());
        }
    }

    [Fact]
    public async Task ProcessNext_LeavesApprovedPaymentWebhookForLegacyOnlineOwner()
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
        const string paymentId = "provider-payment-legacy-owner";
        const string serial = "SERIAL-LEGACY-OWNER";
        var company = new Company { Id = companyId, Name = "Empresa de teste", Cnpj = "00000000000000" };
        var machine = new Machine
        {
            Id = machineId,
            Name = "Máquina com socket legado",
            SerialNumber = serial,
            NormalizedSerialNumber = serial,
            CompanyId = companyId
        };
        var transaction = new PaymentTransaction
        {
            Id = transactionId,
            CompanyId = companyId,
            MachineId = machineId,
            Amount = 10m,
            ApplicationFee = 1m,
            Status = "Pending",
            MercadoPagoPaymentId = paymentId,
            SendTelemetryToMachine = true,
            CreatedAt = DateTime.UtcNow.AddHours(-1)
        };
        var session = new MachineSession
        {
            Id = sessionId,
            CompanyId = companyId,
            MachineId = machineId,
            TransactionId = transactionId,
            State = MachineSessionStates.PaymentPending,
            PaymentDeadlineAt = DateTime.UtcNow.AddMinutes(5)
        };
        var integration = new MercadoPagoIntegration
        {
            CompanyId = companyId,
            IsActive = true,
            AccessToken = EncryptionService.Encrypt("synthetic-access-token"),
            RefreshToken = EncryptionService.Encrypt("synthetic-refresh-token"),
            ClientSecret = EncryptionService.Encrypt("synthetic-client-secret"),
            ClientId = "synthetic-client",
            AccessTokenExpiresAt = DateTime.UtcNow.AddHours(1)
        };
        db.Companies.Add(company);
        db.Machines.Add(machine);
        db.PaymentTransactions.Add(transaction);
        db.MachineSessions.Add(session);
        db.MercadoPagoIntegrations.Add(integration);
        db.MachineConnectionStates.Add(new MachineConnectionState
        {
            MachineId = machineId,
            CompanyId = companyId,
            IsOnline = true,
            ConnectionId = "legacy-owner-connection",
            OwnerNodeId = "legacy-owner-node",
            OwnerConnectionId = "legacy-owner-connection",
            CommandRoutingVersion = null,
            LastSeenAt = DateTime.UtcNow
        });
        db.WebhookInboxMessages.Add(CreateInboxMessage(paymentId, 'e'));
        await db.SaveChangesAsync();

        await using var scopedServices = new ServiceCollection()
            .AddDbContext<AppDbContext>(builder => builder.UseSqlite(connection))
            .BuildServiceProvider();
        var clientFactory = new JsonHttpClientFactory(
            "{\"status\":\"approved\",\"status_detail\":\"accredited\"}");
        var configuration = new ConfigurationBuilder().Build();
        var panelHub = new TelemetryPanelHub(NullLogger<TelemetryPanelHub>.Instance);
        var connections = new TelemetryConnectionManager(
            scopedServices.GetRequiredService<IServiceScopeFactory>(), panelHub,
            NullLogger<TelemetryConnectionManager>.Instance);
        var orchestrator = new SessionOrchestrator(
            db, connections, panelHub, clientFactory, configuration,
            new TestWebHostEnvironment(), NullLogger<SessionOrchestrator>.Instance);
        var processor = new MercadoPagoWebhookProcessor(
            db, orchestrator, configuration, clientFactory,
            NullLogger<MercadoPagoWebhookProcessor>.Instance);

        Assert.False(await processor.ProcessNextAsync(CancellationToken.None));

        var updatedTransaction = await db.PaymentTransactions.AsNoTracking().SingleAsync(item => item.Id == transactionId);
        var updatedSession = await db.MachineSessions.AsNoTracking().SingleAsync(item => item.Id == sessionId);
        var inbox = await db.WebhookInboxMessages.AsNoTracking().SingleAsync();
        Assert.Equal("Pending", updatedTransaction.Status);
        Assert.Equal(MachineSessionStates.PaymentPending, updatedSession.State);
        Assert.Equal("Pending", inbox.Status);
        Assert.Equal(0, inbox.Attempts);
        Assert.Equal(0, clientFactory.RequestCount);
        Assert.Empty(await db.OutboxMessages.AsNoTracking().ToListAsync());
        Assert.Empty(await db.TelemetryCommands.AsNoTracking().ToListAsync());
        Assert.Empty(await db.TelemetryCommandDispatches.AsNoTracking().ToListAsync());
        Assert.Empty(await db.SaleRecords.AsNoTracking().Where(item => item.TransactionId == transactionId).ToListAsync());
    }

    [Fact]
    public async Task ProcessNext_LeavesWebhookPendingForLegacyOnlineOwner()
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
        const string paymentId = "provider-payment-legacy-rejected";
        db.Companies.Add(new Company { Id = companyId, Name = "Empresa de teste", Cnpj = "00000000000000" });
        db.Machines.Add(new Machine
        {
            Id = machineId,
            Name = "Máquina com socket legado",
            SerialNumber = "SERIAL-LEGACY-REJECTED",
            NormalizedSerialNumber = "SERIAL-LEGACY-REJECTED",
            CompanyId = companyId
        });
        db.PaymentTransactions.Add(new PaymentTransaction
        {
            Id = transactionId,
            CompanyId = companyId,
            MachineId = machineId,
            Amount = 10m,
            ApplicationFee = 1m,
            Status = "Pending",
            MercadoPagoPaymentId = paymentId,
            SendTelemetryToMachine = true
        });
        db.MachineSessions.Add(new MachineSession
        {
            Id = sessionId,
            CompanyId = companyId,
            MachineId = machineId,
            TransactionId = transactionId,
            State = MachineSessionStates.PaymentPending,
            PaymentDeadlineAt = DateTime.UtcNow.AddMinutes(5)
        });
        db.MercadoPagoIntegrations.Add(new MercadoPagoIntegration
        {
            CompanyId = companyId,
            IsActive = true,
            AccessToken = EncryptionService.Encrypt("synthetic-access-token"),
            RefreshToken = EncryptionService.Encrypt("synthetic-refresh-token"),
            ClientSecret = EncryptionService.Encrypt("synthetic-client-secret"),
            ClientId = "synthetic-client",
            AccessTokenExpiresAt = DateTime.UtcNow.AddHours(1)
        });
        db.MachineConnectionStates.Add(new MachineConnectionState
        {
            MachineId = machineId,
            CompanyId = companyId,
            IsOnline = true,
            ConnectionId = "legacy-owner-connection",
            OwnerNodeId = "legacy-owner-node",
            OwnerConnectionId = "legacy-owner-connection",
            CommandRoutingVersion = null,
            LastSeenAt = DateTime.UtcNow
        });
        db.WebhookInboxMessages.Add(CreateInboxMessage(paymentId, 'f'));
        await db.SaveChangesAsync();

        await using var scopedServices = new ServiceCollection()
            .AddDbContext<AppDbContext>(builder => builder.UseSqlite(connection))
            .BuildServiceProvider();
        var clientFactory = new JsonHttpClientFactory(
            "{\"status\":\"rejected\",\"status_detail\":\"cc_rejected_bad_filled_other\"}");
        var configuration = new ConfigurationBuilder().Build();
        var panelHub = new TelemetryPanelHub(NullLogger<TelemetryPanelHub>.Instance);
        var connections = new TelemetryConnectionManager(
            scopedServices.GetRequiredService<IServiceScopeFactory>(), panelHub,
            NullLogger<TelemetryConnectionManager>.Instance);
        var orchestrator = new SessionOrchestrator(
            db, connections, panelHub, clientFactory, configuration,
            new TestWebHostEnvironment(), NullLogger<SessionOrchestrator>.Instance);
        var processor = new MercadoPagoWebhookProcessor(
            db, orchestrator, configuration, clientFactory,
            NullLogger<MercadoPagoWebhookProcessor>.Instance);

        Assert.False(await processor.ProcessNextAsync(CancellationToken.None));
        await processor.ReconcilePendingPaymentsAsync(CancellationToken.None);

        var updatedTransaction = await db.PaymentTransactions.AsNoTracking().SingleAsync(item => item.Id == transactionId);
        var updatedSession = await db.MachineSessions.AsNoTracking().SingleAsync(item => item.Id == sessionId);
        var inbox = await db.WebhookInboxMessages.AsNoTracking().SingleAsync();
        Assert.Equal("Pending", updatedTransaction.Status);
        Assert.Null(updatedTransaction.LastReconciliationAt);
        Assert.Equal(MachineSessionStates.PaymentPending, updatedSession.State);
        Assert.Equal("Pending", inbox.Status);
        Assert.Equal(0, inbox.Attempts);
        Assert.Equal(0, clientFactory.RequestCount);
        Assert.Empty(await db.TelemetryCommands.AsNoTracking().ToListAsync());
        Assert.Empty(await db.TelemetryCommandDispatches.AsNoTracking().ToListAsync());
        Assert.Empty(await db.SaleRecords.AsNoTracking().Where(item => item.TransactionId == transactionId).ToListAsync());
        Assert.Empty(await db.OutboxMessages.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Postgres_LegacyOwnerWebhookAndReconciliationRemainForPreviousApi()
    {
        var configuredConnection = Environment.GetEnvironmentVariable("VENDMACHINES_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(configuredConnection))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("VENDMACHINES_REQUIRE_TEST_POSTGRES"), "true",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("O teste de compatibilidade PostgreSQL exige VENDMACHINES_TEST_POSTGRES no CI.");
            return;
        }

        var databaseName = $"vendmachines_legacy_webhook_{Guid.NewGuid():N}";
        var adminBuilder = new NpgsqlConnectionStringBuilder(configuredConnection) { Database = "postgres" };
        var databaseBuilder = new NpgsqlConnectionStringBuilder(configuredConnection) { Database = databaseName };
        await using (var admin = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await admin.OpenAsync();
            await using var createDatabase = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin);
            await createDatabase.ExecuteNonQueryAsync();
        }

        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(databaseBuilder.ConnectionString)
                .Options;
            var companyId = Guid.NewGuid();
            var transactionId = Guid.NewGuid();
            var sessionId = Guid.NewGuid();
            const string paymentId = "provider-payment-legacy-owner";
            await using var db = new AppDbContext(options);
            await db.Database.EnsureCreatedAsync();
            db.Companies.Add(new Company { Id = companyId, Name = "Empresa sintética" });
            db.Machines.Add(new Machine
            {
                Id = "machine-legacy-postgres",
                Name = "Máquina legada",
                SerialNumber = "SERIAL-LEGACY-POSTGRES",
                NormalizedSerialNumber = "SERIAL-LEGACY-POSTGRES",
                CompanyId = companyId
            });
            db.PaymentTransactions.Add(new PaymentTransaction
            {
                Id = transactionId,
                CompanyId = companyId,
                MachineId = "machine-legacy-postgres",
                Amount = 10m,
                ApplicationFee = 1m,
                Status = "Pending",
                MercadoPagoPaymentId = paymentId,
                SendTelemetryToMachine = true,
                CreatedAt = DateTime.UtcNow.AddHours(-1)
            });
            db.MachineSessions.Add(new MachineSession
            {
                Id = sessionId,
                CompanyId = companyId,
                MachineId = "machine-legacy-postgres",
                TransactionId = transactionId,
                State = MachineSessionStates.PaymentPending,
                PaymentDeadlineAt = DateTime.UtcNow.AddMinutes(5)
            });
            db.MachineConnectionStates.Add(new MachineConnectionState
            {
                MachineId = "machine-legacy-postgres",
                CompanyId = companyId,
                IsOnline = true,
                ConnectionId = "legacy-socket",
                OwnerNodeId = "legacy-node",
                OwnerConnectionId = "legacy-socket",
                CommandRoutingVersion = null,
                LastSeenAt = DateTime.UtcNow
            });
            db.MercadoPagoIntegrations.Add(new MercadoPagoIntegration
            {
                CompanyId = companyId,
                IsActive = true,
                AccessToken = EncryptionService.Encrypt("synthetic-access-token"),
                RefreshToken = EncryptionService.Encrypt("synthetic-refresh-token"),
                ClientSecret = EncryptionService.Encrypt("synthetic-client-secret"),
                ClientId = "synthetic-client",
                AccessTokenExpiresAt = DateTime.UtcNow.AddHours(1)
            });
            db.WebhookInboxMessages.Add(CreateInboxMessage(paymentId, 'g'));
            await db.SaveChangesAsync();

            var clientFactory = new JsonHttpClientFactory("{\"status\":\"rejected\"}");
            var configuration = new ConfigurationBuilder().Build();
            var panelHub = new TelemetryPanelHub(NullLogger<TelemetryPanelHub>.Instance);
            await using var scopedServices = new ServiceCollection()
                .AddDbContext<AppDbContext>(builder => builder.UseNpgsql(databaseBuilder.ConnectionString))
                .BuildServiceProvider();
            var connections = new TelemetryConnectionManager(
                scopedServices.GetRequiredService<IServiceScopeFactory>(),
                panelHub,
                NullLogger<TelemetryConnectionManager>.Instance);
            var orchestrator = new SessionOrchestrator(
                db, connections, panelHub, clientFactory, configuration,
                new TestWebHostEnvironment(), NullLogger<SessionOrchestrator>.Instance);
            var processor = new MercadoPagoWebhookProcessor(
                db, orchestrator, configuration, clientFactory,
                NullLogger<MercadoPagoWebhookProcessor>.Instance);

            Assert.False(await processor.ProcessNextAsync(CancellationToken.None));
            await processor.ReconcilePendingPaymentsAsync(CancellationToken.None);

            db.ChangeTracker.Clear();
            var inbox = await db.WebhookInboxMessages.AsNoTracking().SingleAsync();
            var transaction = await db.PaymentTransactions.AsNoTracking().SingleAsync();
            var session = await db.MachineSessions.AsNoTracking().SingleAsync();
            Assert.Equal("Pending", inbox.Status);
            Assert.Equal(0, inbox.Attempts);
            Assert.Equal("Pending", transaction.Status);
            Assert.Null(transaction.LastReconciliationAt);
            Assert.Equal(MachineSessionStates.PaymentPending, session.State);
            Assert.Equal(0, clientFactory.RequestCount);
            Assert.Empty(await db.TelemetryCommandDispatches.AsNoTracking().ToListAsync());
        }
        finally
        {
            await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
            await admin.OpenAsync();
            await using var dropDatabase = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin);
            await dropDatabase.ExecuteNonQueryAsync();
        }
    }

    private static WebhookInboxMessage CreateInboxMessage(string paymentId, char eventKeyPrefix) => new()
    {
        EventKey = new string(eventKeyPrefix, 64),
        PaymentId = paymentId,
        EventType = "payment",
        EventAction = "payment.updated",
        Format = "webhook_v1",
        AuthenticationMode = "hmac_verified"
    };

    private sealed class JsonHttpClientFactory(
        string responseBody,
        Func<HttpRequestMessage, HttpStatusCode>? statusCodeProvider = null) : IHttpClientFactory
    {
        private int _requestCount;
        public int RequestCount => Volatile.Read(ref _requestCount);

        public HttpClient CreateClient(string name) => new(new JsonResponseHandler(responseBody,
            request =>
            {
                Interlocked.Increment(ref _requestCount);
                return statusCodeProvider?.Invoke(request) ?? HttpStatusCode.OK;
            }));
    }

    private sealed class JsonResponseHandler(
        string responseBody,
        Func<HttpRequestMessage, HttpStatusCode> statusCodeProvider) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var statusCode = statusCodeProvider(request);
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            });
        }
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
