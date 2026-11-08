using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using VendingMachines.Api.Data;
using VendingMachines.Api.Controllers;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class SessionPaymentResultConcurrencyTests
{
    [Fact]
    public async Task ConcurrentMenuCompletionClosesSessionOnce()
    {
        var connectionString = Environment.GetEnvironmentVariable("VENDMACHINES_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("VENDMACHINES_REQUIRE_TEST_POSTGRES"), "true", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("O teste concorrente do menu exige PostgreSQL no CI.");
            return;
        }
        var databaseName = $"menu_sequence_{Guid.NewGuid():N}";
        var adminConnection = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres", Pooling = false }.ConnectionString;
        var testConnection = new NpgsqlConnectionStringBuilder(connectionString) { Database = databaseName, Pooling = false }.ConnectionString;
        await using (var admin = new NpgsqlConnection(adminConnection))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin);
            await create.ExecuteNonQueryAsync();
        }
        try
        {
            var company = Guid.NewGuid();
            var sessionId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            const string machineId = "menu-concurrency-machine";
            await using (var setup = CreateDbContext(testConnection))
            {
                await setup.Database.EnsureCreatedAsync();
                setup.Companies.Add(new Company { Id = company, Name = "Empresa sintética", Cnpj = "00000000000000" });
                setup.Machines.Add(new Machine { Id = machineId, Name = "Máquina", SerialNumber = "MENU-CONCURRENCY", CompanyId = company });
                setup.MachineSessions.Add(new MachineSession { Id = sessionId, MachineId = machineId, CompanyId = company, State = MachineSessionStates.AwaitingSelection });
                setup.MachineConnectionStates.Add(new MachineConnectionState { MachineId = machineId, CompanyId = company,
                    ConnectionId = "connection", MdbStatus = MdbStatuses.Inactive, MdbStatusUpdatedAt = now.AddSeconds(-1),
                    MdbMenuPhase = "inactive", MdbMenuConnectionId = "connection", MdbMenuSessionId = sessionId, MdbMenuUpdatedAt = now.AddSeconds(-1) });
                await setup.SaveChangesAsync();
            }
            await using var first = CreateDbContext(testConnection);
            await using var second = CreateDbContext(testConnection);
            var message = new InboundDeviceMessage(machineId, company, "MENU-CONCURRENCY", "connection", 7,
                "{\"command\":\"status\",\"status\":\"enabled_state\"}", now);
            await Task.WhenAll(CreateOrchestrator(first).HandleDeviceMessageAsync(message, CancellationToken.None),
                CreateOrchestrator(second).HandleDeviceMessageAsync(message, CancellationToken.None));
            await using var verify = CreateDbContext(testConnection);
            var session = await verify.MachineSessions.SingleAsync();
            Assert.Equal(MachineSessionStates.ClosedByDeviceBeforeSelection, session.State);
            Assert.Equal(1, await verify.TelemetryEvents.CountAsync(item => item.EventType == "session.menu_closed"));
            Assert.Equal(0, await verify.PaymentTransactions.CountAsync());
        }
        finally
        {
            await using var admin = new NpgsqlConnection(adminConnection);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task PostgresInboxCompletionFailureReplaysDeliveryWithoutDuplicateSaleOrStock()
    {
        var connectionString = Environment.GetEnvironmentVariable("VENDMACHINES_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("VENDMACHINES_REQUIRE_TEST_POSTGRES"), "true", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("O teste de reprocessamento da entrega exige PostgreSQL no CI.");
            return;
        }
        var databaseName = $"delivery_inbox_retry_{Guid.NewGuid():N}";
        var adminConnection = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres", Pooling = false }.ConnectionString;
        var testConnection = new NpgsqlConnectionStringBuilder(connectionString) { Database = databaseName, Pooling = false }.ConnectionString;
        await using (var admin = new NpgsqlConnection(adminConnection))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin);
            await create.ExecuteNonQueryAsync();
        }
        try
        {
            var company = Guid.NewGuid();
            var transaction = Guid.NewGuid();
            const string machine = "replay-delivery-machine";
            await using (var setup = CreateDbContext(testConnection))
            {
                await setup.Database.EnsureCreatedAsync();
                setup.Companies.Add(new Company { Id = company, Name = "Empresa sintética", Cnpj = "00000000000000" });
                setup.Machines.Add(new Machine { Id = machine, Name = "Máquina sintética", SerialNumber = "REPLAY-DELIVERY", CompanyId = company });
                setup.ProductTypes.Add(new ProductType { Id = "replay-type", Name = "Bebida", CompanyId = company });
                setup.Products.Add(new FullProduct { Id = "replay-product", Code = "RP", Name = "Produto", TypeId = "replay-type", CompanyId = company });
                setup.MachineProductSlots.Add(new MachineProductSlot { CompanyId = company, MachineId = machine, ItemNumber = 1, ProductId = "replay-product", Capacity = 10, CurrentStock = 5, LowStockThreshold = 1 });
                setup.PaymentTransactions.Add(new PaymentTransaction { Id = transaction, CompanyId = company, MachineId = machine, Amount = 10m, Status = "Approved", MercadoPagoPaymentId = $"mock_{transaction:N}" });
                setup.MachineSessions.Add(new MachineSession { Id = Guid.NewGuid(), CompanyId = company, MachineId = machine, TransactionId = transaction, State = MachineSessionStates.AwaitingDeliveryResult, ItemNumber = 1, DeliveryDeadlineAt = DateTime.UtcNow.AddMinutes(1) });
                setup.DeviceMessageInbox.Add(new DeviceMessageInbox { IdempotencyKey = Guid.NewGuid().ToString("N"), CompanyId = company, MachineId = machine, Serial = "REPLAY-DELIVERY", ConnectionId = "connection", MessageId = 1, DataJson = "{\"command\":\"vend\",\"type\":\"success\"}", ReceivedAt = DateTime.UtcNow, Status = "Pending" });
                await setup.SaveChangesAsync();
                await setup.Database.ExecuteSqlRawAsync("""
                    CREATE FUNCTION inbox_test_completion_failure() RETURNS trigger LANGUAGE plpgsql AS $$
                    BEGIN IF NEW."Status" = 'Processed' THEN RAISE EXCEPTION 'synthetic completion failure'; END IF; RETURN NEW; END $$;
                    CREATE TRIGGER inbox_test_completion_failure BEFORE UPDATE ON "DeviceMessageInbox"
                    FOR EACH ROW EXECUTE FUNCTION inbox_test_completion_failure();
                    """);
            }
            await using (var db = CreateDbContext(testConnection))
            {
                var processor = new DeviceMessageInboxProcessor(db, CreateOrchestrator(db), NullLogger<DeviceMessageInboxProcessor>.Instance);
                Assert.True(await processor.ProcessNextAsync(CancellationToken.None));
            }
            await using (var verify = CreateDbContext(testConnection))
            {
                Assert.Equal("Retry", (await verify.DeviceMessageInbox.AsNoTracking().SingleAsync()).Status);
                Assert.Equal(1, await verify.SaleRecords.CountAsync(row => row.TransactionId == transaction));
                Assert.Equal(1, await verify.InventoryMovements.CountAsync(row => row.MachineId == machine));
                Assert.Equal(4, (await verify.MachineProductSlots.AsNoTracking().SingleAsync()).CurrentStock);
                await verify.Database.ExecuteSqlRawAsync("DROP TRIGGER inbox_test_completion_failure ON \"DeviceMessageInbox\"; DROP FUNCTION inbox_test_completion_failure();");
                await verify.DeviceMessageInbox.ExecuteUpdateAsync(setters => setters.SetProperty(row => row.NextAttemptAt, DateTime.UtcNow.AddSeconds(-1)));
            }
            await using (var retry = CreateDbContext(testConnection))
            {
                var processor = new DeviceMessageInboxProcessor(retry, CreateOrchestrator(retry), NullLogger<DeviceMessageInboxProcessor>.Instance);
                Assert.True(await processor.ProcessNextAsync(CancellationToken.None));
            }
            await using var final = CreateDbContext(testConnection);
            Assert.Equal("Processed", (await final.DeviceMessageInbox.AsNoTracking().SingleAsync()).Status);
            Assert.Equal(1, await final.SaleRecords.CountAsync(row => row.TransactionId == transaction));
            Assert.Equal(1, await final.InventoryMovements.CountAsync(row => row.MachineId == machine));
            Assert.Equal(4, (await final.MachineProductSlots.AsNoTracking().SingleAsync()).CurrentStock);
        }
        finally
        {
            await using var admin = new NpgsqlConnection(adminConnection);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task ConcurrentPaymentApprovalsSendOnlyOneSaleCommand()
    {
        var connectionString = Environment.GetEnvironmentVariable("VENDMACHINES_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            if (string.Equals(
                    Environment.GetEnvironmentVariable("VENDMACHINES_REQUIRE_TEST_POSTGRES"),
                    "true",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("O teste concorrente de aprovação exige VENDMACHINES_TEST_POSTGRES no CI.");
            }

            return;
        }

        var databaseName = $"payment_result_{Guid.NewGuid():N}";
        var adminBuilder = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" };
        var adminConnectionString = adminBuilder.ConnectionString;
        await using (var admin = new NpgsqlConnection(adminConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin);
            await create.ExecuteNonQueryAsync();
        }

        var scopedConnectionString = new NpgsqlConnectionStringBuilder(connectionString) { Database = databaseName }.ConnectionString;
        try
        {
            var companyId = Guid.NewGuid();
            var machineId = $"machine-{Guid.NewGuid():N}";
            var transactionId = Guid.NewGuid();
            var sessionId = Guid.NewGuid();
            await using (var setup = CreateDbContext(scopedConnectionString))
            {
                await setup.Database.EnsureCreatedAsync();
                setup.Companies.Add(new Company
                {
                    Id = companyId,
                    Name = "Empresa concorrente",
                    Cnpj = "00000000000000"
                });
                setup.Machines.Add(new Machine
                {
                    Id = machineId,
                    Name = "Máquina concorrente",
                    SerialNumber = $"SERIAL-{Guid.NewGuid():N}",
                    CompanyId = companyId
                });
                setup.PaymentTransactions.Add(new PaymentTransaction
                {
                    Id = transactionId,
                    CompanyId = companyId,
                    MachineId = machineId,
                    Amount = 10m,
                    Status = "Pending",
                    MercadoPagoPaymentId = $"mock_{transactionId:N}"
                });
                setup.MachineSessions.Add(new MachineSession
                {
                    Id = sessionId,
                    CompanyId = companyId,
                    MachineId = machineId,
                    TransactionId = transactionId,
                    State = MachineSessionStates.PaymentPending,
                    PaymentDeadlineAt = DateTime.UtcNow.AddMinutes(5)
                });
                await setup.SaveChangesAsync();
            }

            var barrier = new InitialSessionReadBarrier();
            await using var firstDb = CreateDbContext(scopedConnectionString, barrier);
            await using var secondDb = CreateDbContext(scopedConnectionString, barrier);
            var firstOrchestrator = CreateOrchestrator(firstDb);
            var secondOrchestrator = CreateOrchestrator(secondDb);

            await Task.WhenAll(
                firstOrchestrator.HandlePaymentResultAsync(transactionId, true, "webhook-a", CancellationToken.None),
                secondOrchestrator.HandlePaymentResultAsync(transactionId, true, "webhook-b", CancellationToken.None));

            await using var verification = CreateDbContext(scopedConnectionString);
            Assert.Equal(1, await verification.TelemetryCommands.CountAsync(command =>
                command.SessionId == sessionId && command.Command == "VENDA_APROVADA"));
            Assert.Equal(1, await verification.TelemetryEvents.CountAsync(item =>
                item.SessionId == sessionId && item.EventType == "payment.approved"));
            Assert.Equal(1, await verification.TelemetryEvents.CountAsync(item =>
                item.SessionId == sessionId && item.EventType == "payment.duplicate_result"));
            Assert.Equal(1, await verification.OutboxMessages.CountAsync(item =>
                item.SessionId == sessionId && item.MessageType == "refund_payment"));
        }
        finally
        {
            await using var admin = new NpgsqlConnection(adminConnectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task ConcurrentPaymentApprovalAndTimeoutNeverSendApproveAndDenyCommandsTogether()
    {
        var connectionString = Environment.GetEnvironmentVariable("VENDMACHINES_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            if (string.Equals(
                    Environment.GetEnvironmentVariable("VENDMACHINES_REQUIRE_TEST_POSTGRES"),
                    "true",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("O teste concorrente entre aprovação e timeout exige VENDMACHINES_TEST_POSTGRES no CI.");
            }

            return;
        }

        var databaseName = $"payment_timeout_{Guid.NewGuid():N}";
        var adminBuilder = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" };
        var adminConnectionString = adminBuilder.ConnectionString;
        await using (var admin = new NpgsqlConnection(adminConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin);
            await create.ExecuteNonQueryAsync();
        }

        var scopedConnectionString = new NpgsqlConnectionStringBuilder(connectionString) { Database = databaseName }.ConnectionString;
        try
        {
            var companyId = Guid.NewGuid();
            var machineId = $"machine-{Guid.NewGuid():N}";
            var transactionId = Guid.NewGuid();
            var sessionId = Guid.NewGuid();
            await using (var setup = CreateDbContext(scopedConnectionString))
            {
                await setup.Database.EnsureCreatedAsync();
                setup.Companies.Add(new Company
                {
                    Id = companyId,
                    Name = "Empresa concorrente",
                    Cnpj = "00000000000000"
                });
                setup.Machines.Add(new Machine
                {
                    Id = machineId,
                    Name = "Máquina concorrente",
                    SerialNumber = $"SERIAL-{Guid.NewGuid():N}",
                    CompanyId = companyId
                });
                setup.PaymentTransactions.Add(new PaymentTransaction
                {
                    Id = transactionId,
                    CompanyId = companyId,
                    MachineId = machineId,
                    Amount = 10m,
                    Status = "Pending",
                    MercadoPagoPaymentId = $"mock_{transactionId:N}"
                });
                setup.MachineSessions.Add(new MachineSession
                {
                    Id = sessionId,
                    CompanyId = companyId,
                    MachineId = machineId,
                    TransactionId = transactionId,
                    State = MachineSessionStates.PaymentPending,
                    PaymentDeadlineAt = DateTime.UtcNow.AddMinutes(-1)
                });
                await setup.SaveChangesAsync();
            }

            var barrier = new InitialSessionReadBarrier();
            await using var firstDb = CreateDbContext(scopedConnectionString, barrier);
            await using var secondDb = CreateDbContext(scopedConnectionString, barrier);
            var firstOrchestrator = CreateOrchestrator(firstDb);
            var secondOrchestrator = CreateOrchestrator(secondDb);

            await Task.WhenAll(
                firstOrchestrator.HandlePaymentResultAsync(transactionId, true, "provider_approved", CancellationToken.None),
                secondOrchestrator.ProcessDeadlinesAsync(CancellationToken.None));
            await firstOrchestrator.ProcessOutboxAsync(CancellationToken.None);
            await secondOrchestrator.ProcessOutboxAsync(CancellationToken.None);

            await using var verification = CreateDbContext(scopedConnectionString);
            var session = await verification.MachineSessions.AsNoTracking().SingleAsync(item => item.Id == sessionId);
            var payment = await verification.PaymentTransactions.AsNoTracking().SingleAsync(item => item.Id == transactionId);
            var approvedCommandCount = await verification.TelemetryCommands.CountAsync(item =>
                item.SessionId == sessionId && item.Command == "VENDA_APROVADA");
            var deniedCommandCount = await verification.TelemetryCommands.CountAsync(item =>
                item.SessionId == sessionId && item.Command == "VENDA_NEGADA");

            Assert.Equal(MachineSessionStates.Refunded, session.State);
            Assert.Equal("Refunded", payment.Status);
            Assert.True(approvedCommandCount == 0 || deniedCommandCount == 0);
            Assert.Equal(1, await verification.OutboxMessages.CountAsync(item =>
                item.SessionId == sessionId && item.MessageType == "refund_payment"));
            Assert.Equal(0, await verification.SaleRecords.CountAsync(item => item.TransactionId == transactionId));
        }
        finally
        {
            await using var admin = new NpgsqlConnection(adminConnectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("timeout")]
    [InlineData("end_session")]
    public async Task ConcurrentDeliverySuccessAndCompetingOutcomePersistOnlyOneFinancialOutcome(string competingResult)
    {
        var connectionString = Environment.GetEnvironmentVariable("VENDMACHINES_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            if (string.Equals(
                    Environment.GetEnvironmentVariable("VENDMACHINES_REQUIRE_TEST_POSTGRES"),
                    "true",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("O teste concorrente de entrega exige VENDMACHINES_TEST_POSTGRES no CI.");
            }

            return;
        }

        var databaseName = $"delivery_result_{Guid.NewGuid():N}";
        var adminBuilder = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" };
        var adminConnectionString = adminBuilder.ConnectionString;
        await using (var admin = new NpgsqlConnection(adminConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin);
            await create.ExecuteNonQueryAsync();
        }

        var scopedConnectionString = new NpgsqlConnectionStringBuilder(connectionString) { Database = databaseName }.ConnectionString;
        try
        {
            var companyId = Guid.NewGuid();
            var machineId = $"machine-{Guid.NewGuid():N}";
            var transactionId = Guid.NewGuid();
            var sessionId = Guid.NewGuid();
            await using (var setup = CreateDbContext(scopedConnectionString))
            {
                await setup.Database.EnsureCreatedAsync();
                setup.Companies.Add(new Company
                {
                    Id = companyId,
                    Name = "Empresa concorrente",
                    Cnpj = "00000000000000"
                });
                setup.Machines.Add(new Machine
                {
                    Id = machineId,
                    Name = "Máquina concorrente",
                    SerialNumber = $"SERIAL-{Guid.NewGuid():N}",
                    CompanyId = companyId
                });
                setup.ProductTypes.Add(new ProductType
                {
                    Id = $"type-{companyId:N}",
                    Name = "Bebidas",
                    CompanyId = companyId
                });
                setup.Products.Add(new FullProduct
                {
                    Id = $"product-{companyId:N}",
                    Code = "W500",
                    Name = "Água 500 ml",
                    TypeId = $"type-{companyId:N}",
                    CompanyId = companyId,
                    Cost = 1m
                });
                setup.MachineProductSlots.Add(new MachineProductSlot
                {
                    CompanyId = companyId,
                    MachineId = machineId,
                    ItemNumber = 1,
                    ProductId = $"product-{companyId:N}",
                    Capacity = 10,
                    CurrentStock = 5,
                    LowStockThreshold = 1
                });
                setup.PaymentTransactions.Add(new PaymentTransaction
                {
                    Id = transactionId,
                    CompanyId = companyId,
                    MachineId = machineId,
                    Amount = 10m,
                    Status = "Approved",
                    MercadoPagoPaymentId = $"mock_{transactionId:N}"
                });
                setup.MachineSessions.Add(new MachineSession
                {
                    Id = sessionId,
                    CompanyId = companyId,
                    MachineId = machineId,
                    TransactionId = transactionId,
                    State = MachineSessionStates.AwaitingDeliveryResult,
                    ItemNumber = 1,
                    DeliveryDeadlineAt = competingResult == "timeout"
                        ? DateTime.UtcNow.AddMinutes(-1)
                        : DateTime.UtcNow.AddMinutes(1)
                });
                await setup.SaveChangesAsync();
            }

            var barrier = new InitialSessionReadBarrier();
            await using var firstDb = CreateDbContext(scopedConnectionString, barrier);
            await using var secondDb = CreateDbContext(scopedConnectionString, barrier);
            var firstOrchestrator = CreateOrchestrator(firstDb);
            var secondOrchestrator = CreateOrchestrator(secondDb);
            var receivedAt = DateTime.UtcNow;

            var successfulDelivery = firstOrchestrator.HandleDeviceMessageAsync(new InboundDeviceMessage(
                machineId, companyId, "SERIAL-TEST", "connection-a", 1,
                "{\"command\":\"vend\",\"type\":\"success\"}", receivedAt), CancellationToken.None);
            var competingOutcome = competingResult == "failure"
                ? secondOrchestrator.HandleDeviceMessageAsync(new InboundDeviceMessage(
                    machineId, companyId, "SERIAL-TEST", "connection-b", 1,
                    "{\"command\":\"vend\",\"type\":\"failure\",\"reason\":\"mechanism_error\"}", receivedAt.AddTicks(1)), CancellationToken.None)
                : competingResult == "timeout"
                    ? secondOrchestrator.ProcessDeadlinesAsync(CancellationToken.None)
                    : secondOrchestrator.HandleDeviceMessageAsync(new InboundDeviceMessage(
                        machineId, companyId, "SERIAL-TEST", "connection-b", 1,
                        "{\"command\":\"pool\",\"type\":\"end_session\"}", receivedAt.AddTicks(1)), CancellationToken.None);
            await Task.WhenAll(successfulDelivery, competingOutcome);
            await firstOrchestrator.ProcessOutboxAsync(CancellationToken.None);

            await using var verification = CreateDbContext(scopedConnectionString);
            var completed = await verification.MachineSessions.AsNoTracking().SingleAsync(item => item.Id == sessionId);
            var successCount = await verification.TelemetryEvents.CountAsync(item =>
                item.SessionId == sessionId && item.EventType == "delivery.success");
            var failureCount = await verification.TelemetryEvents.CountAsync(item =>
                item.SessionId == sessionId && item.EventType == "delivery.failure");
            if (competingResult == "failure")
                Assert.Equal(1, successCount + failureCount);
            else
                Assert.InRange(successCount, 0, 1);

            if (completed.State == MachineSessionStates.Completed)
            {
                Assert.Equal(1, successCount);
                Assert.Equal(0, failureCount);
                Assert.Equal(1, await verification.SaleRecords.CountAsync(item => item.TransactionId == transactionId));
                Assert.Equal(1, await verification.InventoryMovements.CountAsync(item => item.MachineId == machineId));
                Assert.Equal(4, await verification.MachineProductSlots.Where(item => item.MachineId == machineId)
                    .Select(item => item.CurrentStock).SingleAsync());
                Assert.Equal(0, await verification.OutboxMessages.CountAsync(item =>
                    item.SessionId == sessionId && item.MessageType == "refund_payment"));
            }
            else
            {
                Assert.Equal(MachineSessionStates.Refunded, completed.State);
                Assert.Equal(0, successCount);
                Assert.Equal(competingResult == "failure" ? 1 : 0, failureCount);
                var conflictingDeliveries = await verification.TelemetryEvents.CountAsync(item =>
                    item.SessionId == sessionId && item.EventType == "delivery.conflicting_result");
                Assert.Equal(1, conflictingDeliveries);
                var sale = await verification.SaleRecords.AsNoTracking()
                    .SingleAsync(item => item.TransactionId == transactionId);
                Assert.Equal("Refunded", sale.FinancialStatus);
                Assert.Equal(10m, sale.RefundedAmount);
                Assert.Equal(1, await verification.InventoryMovements.CountAsync(item => item.MachineId == machineId));
                Assert.Equal(4, await verification.MachineProductSlots.Where(item => item.MachineId == machineId)
                    .Select(item => item.CurrentStock).SingleAsync());
                Assert.Equal(competingResult == "failure" ? 1 : 0,
                    await verification.DeliveryFailures.CountAsync(item => item.TransactionId == transactionId));
                Assert.Equal(1, await verification.OutboxMessages.CountAsync(item =>
                    item.SessionId == sessionId && item.MessageType == "refund_payment"));
            }
        }
        finally
        {
            await using var admin = new NpgsqlConnection(adminConnectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task ConcurrentDeliveryAndRestockPreserveBothInventoryMovements()
    {
        var connectionString = Environment.GetEnvironmentVariable("VENDMACHINES_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("VENDMACHINES_REQUIRE_TEST_POSTGRES"), "true", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("O teste de baixa concorrente de estoque exige VENDMACHINES_TEST_POSTGRES no CI.");
            return;
        }

        var databaseName = $"delivery_shared_inventory_{Guid.NewGuid():N}";
        var adminConnectionString = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" }.ConnectionString;
        var scopedConnectionString = new NpgsqlConnectionStringBuilder(connectionString) { Database = databaseName }.ConnectionString;
        await using (var admin = new NpgsqlConnection(adminConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin);
            await create.ExecuteNonQueryAsync();
        }

        var companyId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        const string machineId = "shared-inventory-machine";
        const string productId = "shared-inventory-product";
        try
        {
            await using (var setup = CreateDbContext(scopedConnectionString))
            {
                await setup.Database.EnsureCreatedAsync();
                setup.Companies.Add(new Company
                {
                    Id = companyId,
                    Name = "Empresa de vendas concorrentes",
                    Cnpj = "00000000000000"
                });
                setup.Machines.Add(new Machine
                {
                    Id = machineId,
                    Name = "Máquina concorrente",
                    SerialNumber = "SERIAL-SHARED-INVENTORY",
                    CompanyId = companyId
                });
                setup.ProductTypes.Add(new ProductType
                {
                    Id = $"shared-type-{companyId:N}",
                    Name = "Bebidas",
                    CompanyId = companyId
                });
                setup.Products.Add(new FullProduct
                {
                    Id = productId,
                    Code = "SHARED-DRINK",
                    Name = "Bebida compartilhada",
                    TypeId = $"shared-type-{companyId:N}",
                    CompanyId = companyId,
                    Cost = 1m
                });
                setup.MachineProductSlots.Add(new MachineProductSlot
                {
                    CompanyId = companyId,
                    MachineId = machineId,
                    ItemNumber = 1,
                    ProductId = productId,
                    Capacity = 10,
                    CurrentStock = 2,
                    LowStockThreshold = 1
                });
                setup.PaymentTransactions.Add(new PaymentTransaction
                {
                    Id = transactionId,
                    CompanyId = companyId,
                    MachineId = machineId,
                    Amount = 10m,
                    Status = "Approved",
                    MercadoPagoPaymentId = $"mock_{transactionId:N}"
                });
                setup.MachineSessions.Add(new MachineSession
                {
                    Id = sessionId,
                    CompanyId = companyId,
                    MachineId = machineId,
                    TransactionId = transactionId,
                    State = MachineSessionStates.AwaitingDeliveryResult,
                    ItemNumber = 1
                });
                await setup.SaveChangesAsync();
            }

            var slotReadBarrier = new FirstInventorySlotReadBarrier();
            await using var saleDb = CreateDbContext(scopedConnectionString, slotReadBarrier);
            var orchestrator = CreateOrchestrator(saleDb);
            var deliveredAt = DateTime.UtcNow;
            var deliveryTask = orchestrator.HandleDeviceMessageAsync(new InboundDeviceMessage(
                machineId, companyId, "SERIAL-SHARED-INVENTORY", "connection-a", 1,
                "{\"command\":\"vend\",\"type\":\"success\"}", deliveredAt), CancellationToken.None);
            await slotReadBarrier.FirstSlotRead.WaitAsync(TimeSpan.FromSeconds(5));

            await using var restockDb = CreateDbContext(scopedConnectionString);
            var controller = new InventoryController(restockDb, new NoOpAuditLogService())
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity(
                            [new Claim("company_id", companyId.ToString())], "test"))
                    }
                }
            };
            Task<IActionResult>? restockTask = null;
            var waitingMachineOperations = 0;
            var restockCompletedBeforeDeliveryCommit = false;
            try
            {
                restockTask = controller.Restock(
                    machineId, 1, new InventoryQuantityRequest(5, "Reposição concorrente"), CancellationToken.None);
                var lockDeadline = DateTime.UtcNow.AddSeconds(3);
                await using (var observer = new NpgsqlConnection(scopedConnectionString))
                {
                    await observer.OpenAsync();
                    while (DateTime.UtcNow < lockDeadline && !restockTask.IsCompleted)
                    {
                        await using var waitQuery = new NpgsqlCommand("""
                            SELECT COUNT(*) FROM pg_stat_activity
                            WHERE datname = current_database()
                              AND wait_event_type = 'Lock'
                              AND query ILIKE '%FROM "Machines"%'
                              AND query ILIKE '%FOR NO KEY UPDATE%'
                            """, observer);
                        waitingMachineOperations = Convert.ToInt32(await waitQuery.ExecuteScalarAsync());
                        if (waitingMachineOperations > 0) break;
                        await Task.Delay(TimeSpan.FromMilliseconds(25));
                    }
                }
                restockCompletedBeforeDeliveryCommit = restockTask.IsCompleted;
            }
            finally
            {
                slotReadBarrier.Release();
            }
            await deliveryTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(waitingMachineOperations > 0,
                "A reposição concorrente não aguardou o bloqueio transacional da máquina.");
            Assert.False(restockCompletedBeforeDeliveryCommit);
            Assert.NotNull(restockTask);
            var restockDuringSession = Assert.IsType<ConflictObjectResult>(
                await restockTask.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains("active_vend_session", JsonSerializer.Serialize(restockDuringSession.Value));

            await orchestrator.HandleDeviceMessageAsync(new InboundDeviceMessage(
                machineId, companyId, "SERIAL-SHARED-INVENTORY", "connection-a", 2,
                "{\"command\":\"pool\",\"type\":\"end_session\"}", DateTime.UtcNow), CancellationToken.None);
            await using (var closedSessionDb = CreateDbContext(scopedConnectionString))
            {
                Assert.NotNull(await closedSessionDb.MachineSessions.AsNoTracking()
                    .Where(item => item.Id == sessionId)
                    .Select(item => item.ClosedAt)
                    .SingleAsync());
            }

            var restockAfterClose = await controller.Restock(
                machineId, 1, new InventoryQuantityRequest(5, "Reposição após encerramento MDB"), CancellationToken.None);
            Assert.IsType<OkObjectResult>(restockAfterClose);

            await using var verification = CreateDbContext(scopedConnectionString);
            var slot = await verification.MachineProductSlots.AsNoTracking().SingleAsync();
            var sales = await verification.SaleRecords.AsNoTracking().ToListAsync();
            var movements = await verification.InventoryMovements.AsNoTracking().ToListAsync();
            Assert.Equal(6, slot.CurrentStock);
            Assert.Single(sales);
            Assert.Equal(2, movements.Count);
            Assert.Equal(productId, sales[0].ProductId);
            var saleMovement = Assert.Single(movements, movement => movement.MovementType == "Sale");
            var restockMovement = Assert.Single(movements, movement => movement.MovementType == "Restock");
            Assert.Equal(-1, saleMovement.QuantityDelta);
            Assert.Equal(1, saleMovement.QuantityAfter);
            Assert.Equal(5, restockMovement.QuantityDelta);
            Assert.Equal(6, restockMovement.QuantityAfter);
        }
        finally
        {
            await using var admin = new NpgsqlConnection(adminConnectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static AppDbContext CreateDbContext(string connectionString, DbCommandInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString);
        if (interceptor != null)
            options.AddInterceptors(interceptor);
        return new AppDbContext(options.Options);
    }

    private static SessionOrchestrator CreateOrchestrator(AppDbContext db)
    {
        var panelHub = new TelemetryPanelHub(NullLogger<TelemetryPanelHub>.Instance);
        var connections = new TelemetryConnectionManager(
            new UnusedScopeFactory(), panelHub, NullLogger<TelemetryConnectionManager>.Instance);
        return new SessionOrchestrator(
            db,
            connections,
            panelHub,
            new NoRequestHttpClientFactory(),
            new ConfigurationBuilder().Build(),
            new TestWebHostEnvironment(),
            NullLogger<SessionOrchestrator>.Instance);
    }

    private sealed class InitialSessionReadBarrier : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _bothRequestsReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reads;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"MachineSessions\"", StringComparison.Ordinal) &&
                Interlocked.Increment(ref _reads) <= 2)
            {
                if (Volatile.Read(ref _reads) == 2)
                    _bothRequestsReady.TrySetResult();
                await _bothRequestsReady.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }

    private sealed class FirstInventorySlotReadBarrier : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _slotRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _hasWaited;

        public Task FirstSlotRead => _slotRead.Task;
        public void Release() => _release.TrySetResult();

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"MachineProductSlots\"", StringComparison.Ordinal) &&
                Interlocked.Exchange(ref _hasWaited, 1) == 0)
            {
                _slotRead.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }

    private sealed class NoOpAuditLogService : IAuditLogService
    {
        public Task RecordAsync(
            string action,
            string resourceType,
            string? resourceId = null,
            Guid? companyId = null,
            object? metadata = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class UnusedScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("O escopo não é utilizado neste teste.");
    }

    private sealed class NoRequestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new RejectRequestHandler());
    }

    private sealed class RejectRequestHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("O teste não pode acessar serviços externos.");
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
