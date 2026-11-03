using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class OperationalAlertSyncServiceTests
{
    [Fact]
    public async Task SyncIgnoresOperationalRowsWhoseCompanyDoesNotOwnTheMachine()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var companyA = new Company { Id = Guid.NewGuid(), Name = "Empresa A", Cnpj = "11111111000111" };
        var companyB = new Company { Id = Guid.NewGuid(), Name = "Empresa B", Cnpj = "22222222000122" };
        var machineA = new Machine { Id = "machine-a", Name = "Máquina A", SerialNumber = "A", CompanyId = companyA.Id };
        var machineB = new Machine { Id = "machine-b", Name = "Máquina B", SerialNumber = "B", CompanyId = companyB.Id };
        var productType = new ProductType { Id = "type-b", Name = "Bebidas", CompanyId = companyB.Id };
        var productB = new FullProduct
        {
            Id = "product-b", Name = "Produto B", Code = "B", TypeId = productType.Id, CompanyId = companyB.Id
        };
        db.Companies.AddRange(companyA, companyB);
        db.Machines.AddRange(machineA, machineB);
        db.ProductTypes.Add(productType);
        db.Products.Add(productB);
        db.MachineConnectionStates.AddRange(
            new MachineConnectionState
            {
                MachineId = machineA.Id, CompanyId = companyB.Id, MonitoringEnabled = true,
                IsOnline = true, LastSeenAt = DateTime.UtcNow
            },
            new MachineConnectionState { MachineId = machineB.Id, CompanyId = companyB.Id, MonitoringEnabled = false });
        db.MachineProductSlots.Add(new MachineProductSlot
        {
            CompanyId = companyB.Id, MachineId = machineA.Id, ItemNumber = 1, ProductId = productB.Id,
            Capacity = 10, CurrentStock = 0, LowStockThreshold = 2
        });
        db.MachineProductSlots.Add(new MachineProductSlot
        {
            CompanyId = companyA.Id, MachineId = machineA.Id, ItemNumber = 2, ProductId = productB.Id,
            Capacity = 10, CurrentStock = 0, LowStockThreshold = 2
        });
        db.MachineSessions.Add(new MachineSession
        {
            CompanyId = companyB.Id, MachineId = machineA.Id, State = MachineSessionStates.DeliveryFailed
        });
        db.PaymentTransactions.Add(new PaymentTransaction
        {
            CompanyId = companyB.Id, MachineId = machineA.Id, Amount = 10m, Status = "Pending",
            MercadoPagoPaymentId = "real-payment", CreatedAt = DateTime.UtcNow.AddMinutes(-10)
        });
        await db.SaveChangesAsync();

        var service = new OperationalAlertSyncService(db, NullLogger<OperationalAlertSyncService>.Instance);
        await service.SyncAsync(CancellationToken.None);
        var alerts = await db.OperationalAlerts.AsNoTracking().ToListAsync();

        Assert.Contains(alerts, alert => alert.CompanyId == companyA.Id &&
            alert.MachineId == machineA.Id && alert.Type == "TelemetryUnmonitored");
        Assert.DoesNotContain(alerts, alert => alert.MachineId == machineA.Id && alert.Type == "TelemetryOwnerUnknown");
        Assert.DoesNotContain(alerts, alert => alert.CompanyId == companyB.Id && alert.MachineId == machineA.Id);
        Assert.Contains(alerts, alert => alert.CompanyId == companyA.Id && alert.MachineId == machineA.Id && alert.Type == "LowStock");
        Assert.DoesNotContain(alerts, alert => alert.Description.Contains("Produto B", StringComparison.Ordinal));
        Assert.DoesNotContain(alerts, alert => alert.Type is "SessionFailure" or "PaymentPending");
    }

    [Fact]
    public async Task Sync_DeduplicatesActiveConditionsAndResolvesThemWhenTheyClear()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var companyId = Guid.NewGuid();
        var machine = new Machine
        {
            Id = "alert-machine",
            Name = "Máquina A",
            SerialNumber = "alert-machine",
            CompanyId = companyId
        };
        var productType = new ProductType { Id = "alert-product-type", Name = "Bebidas" };
        var product = new FullProduct
        {
            Id = "alert-product",
            Code = "ALERT",
            Name = "Suco",
            TypeId = productType.Id,
            CompanyId = companyId
        };
        var connectionState = new MachineConnectionState
        {
            MachineId = machine.Id,
            CompanyId = companyId,
            MonitoringEnabled = false
        };
        var slot = new MachineProductSlot
        {
            CompanyId = companyId,
            MachineId = machine.Id,
            ItemNumber = 3,
            ProductId = product.Id,
            Capacity = 10,
            CurrentStock = 1,
            LowStockThreshold = 2
        };
        var pendingPayment = new PaymentTransaction
        {
            CompanyId = companyId,
            MachineId = machine.Id,
            Amount = 10m,
            Status = "Pending",
            MercadoPagoPaymentId = "real-payment-123",
            CreatedAt = DateTime.UtcNow.AddMinutes(-15)
        };
        var failedSession = new MachineSession
        {
            CompanyId = companyId,
            MachineId = machine.Id,
            State = MachineSessionStates.DeliveryFailed,
            LastEventAt = DateTime.UtcNow.AddMinutes(-12)
        };
        db.Companies.Add(new Company { Id = companyId, Name = "Empresa A", Cnpj = "11111111000111" });
        db.Machines.Add(machine);
        db.ProductTypes.Add(productType);
        db.Products.Add(product);
        db.MachineConnectionStates.Add(connectionState);
        db.MachineProductSlots.Add(slot);
        db.PaymentTransactions.Add(pendingPayment);
        db.MachineSessions.Add(failedSession);
        await db.SaveChangesAsync();

        var service = new OperationalAlertSyncService(db, NullLogger<OperationalAlertSyncService>.Instance);
        await service.SyncAsync(CancellationToken.None);
        var firstRun = await db.OperationalAlerts.AsNoTracking().OrderBy(alert => alert.Type).ToListAsync();
        await service.SyncAsync(CancellationToken.None);
        var secondRun = await db.OperationalAlerts.AsNoTracking().ToListAsync();

        Assert.Equal(4, firstRun.Count);
        Assert.Equal(4, secondRun.Count);
        Assert.All(secondRun, alert => Assert.Equal("Active", alert.Status));
        Assert.Contains(secondRun, alert => alert.Type == "LowStock");
        Assert.Contains(secondRun, alert => alert.Type == "TelemetryUnmonitored");
        Assert.Contains(secondRun, alert => alert.Type == "PaymentPending");
        Assert.Contains(secondRun, alert => alert.Type == "SessionFailure");

        connectionState.MonitoringEnabled = true;
        connectionState.IsOnline = true;
        connectionState.LastSeenAt = DateTime.UtcNow;
        slot.CurrentStock = 8;
        pendingPayment.Status = "Approved";
        failedSession.State = MachineSessionStates.Completed;
        await db.SaveChangesAsync();
        await service.SyncAsync(CancellationToken.None);

        var afterLegacyConnection = await db.OperationalAlerts.AsNoTracking().ToListAsync();
        Assert.Equal(5, afterLegacyConnection.Count);
        Assert.Contains(afterLegacyConnection, alert => alert.Type == "TelemetryOwnerUnknown" && alert.Status == "Active");
        await service.SyncAsync(CancellationToken.None);
        Assert.Equal(5, await db.OperationalAlerts.CountAsync());

        connectionState.OwnerNodeId = "owner-node-1";
        connectionState.OwnerConnectionId = "connection-1";
        await db.SaveChangesAsync();
        await service.SyncAsync(CancellationToken.None);

        var stillUnsupported = await db.OperationalAlerts.AsNoTracking().ToListAsync();
        Assert.Contains(stillUnsupported, alert => alert.Type == "TelemetryOwnerUnknown" && alert.Status == "Active");

        connectionState.CommandRoutingVersion = TelemetryConnectionManager.CurrentCommandRoutingVersion;
        await db.SaveChangesAsync();
        await service.SyncAsync(CancellationToken.None);

        var resolved = await db.OperationalAlerts.AsNoTracking().ToListAsync();
        Assert.Equal(5, resolved.Count);
        Assert.All(resolved, alert =>
        {
            Assert.Equal("Resolved", alert.Status);
            Assert.NotNull(alert.ResolvedAt);
        });
    }

    [Fact]
    public async Task Sync_ResolvesIncompleteClosureAlertOnlyAfterAuditedClosureConfirmation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var companyId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var machine = new Machine
        {
            Id = "closure-alert-machine",
            Name = "Máquina com fechamento pendente",
            SerialNumber = "closure-alert-machine",
            CompanyId = companyId
        };
        var session = new MachineSession
        {
            Id = sessionId,
            CompanyId = companyId,
            MachineId = machine.Id,
            State = MachineSessionStates.ClosureIncomplete,
            ClosedAt = DateTime.UtcNow,
            LastEventAt = DateTime.UtcNow
        };
        db.TelemetryEvents.Add(new TelemetryEvent
        {
            CompanyId = companyId,
            MachineId = machine.Id,
            SessionId = session.Id,
            UserId = Guid.NewGuid(),
            EventType = "session.closure_verified",
            Detail = "Responsável confirmou que a máquina foi inspecionada e está ociosa.",
            DataJson = "{\"evidenceReference\":\"vistoria-2026-001\"}",
            CreatedAt = DateTime.UtcNow
        });
        db.Companies.Add(new Company { Id = companyId, Name = "Empresa A", Cnpj = "44444444000144" });
        db.Machines.Add(machine);
        db.MachineSessions.Add(session);
        db.OperationalAlerts.Add(new OperationalAlert
        {
            CompanyId = companyId,
            MachineId = machine.Id,
            SessionId = session.Id,
            DeduplicationKey = $"session:{session.Id}",
            Type = "SessionFailure",
            Severity = "critical",
            Status = "Active",
            Title = "Sessão precisa de análise",
            Description = "A máquina aguarda confirmação física de fechamento."
        });
        await db.SaveChangesAsync();

        var service = new OperationalAlertSyncService(db, NullLogger<OperationalAlertSyncService>.Instance);
        await service.SyncAsync(CancellationToken.None);
        var pendingAlert = await db.OperationalAlerts.SingleAsync(alert => alert.Type == "SessionFailure");
        Assert.Equal("Active", pendingAlert.Status);

        session.State = MachineSessionStates.ClosureVerified;
        await db.SaveChangesAsync();
        await service.SyncAsync(CancellationToken.None);

        var resolvedAlert = await db.OperationalAlerts.SingleAsync(alert => alert.Type == "SessionFailure");
        Assert.Equal("Resolved", resolvedAlert.Status);
        Assert.NotNull(resolvedAlert.ResolvedAt);
        Assert.Contains("vistoria-2026-001", resolvedAlert.Resolution, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sync_UsesMonitoringActivationAsBaselineUntilFirstMachineContact()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var companyId = Guid.NewGuid();
        var machine = new Machine
        {
            Id = "never-connected-machine",
            Name = "Máquina sem primeiro contato",
            SerialNumber = "never-connected-machine",
            CompanyId = companyId
        };
        var connectionState = new MachineConnectionState
        {
            MachineId = machine.Id,
            CompanyId = companyId,
            MonitoringEnabled = true,
            ActivatedAt = DateTime.UtcNow.AddMinutes(-1)
        };
        db.Companies.Add(new Company { Id = companyId, Name = "Empresa A", Cnpj = "33333333000133" });
        db.Machines.Add(machine);
        db.MachineConnectionStates.Add(connectionState);
        await db.SaveChangesAsync();

        var service = new OperationalAlertSyncService(db, NullLogger<OperationalAlertSyncService>.Instance);
        await service.SyncAsync(CancellationToken.None);
        Assert.Empty(await db.OperationalAlerts.ToListAsync());
        Assert.Equal("warning", Assert.Single(new DatabaseDataService(db).GetMachines(companyId)).Status);

        connectionState.ActivatedAt = DateTime.UtcNow.AddMinutes(-3);
        await db.SaveChangesAsync();
        await service.SyncAsync(CancellationToken.None);
        var warningAlert = await db.OperationalAlerts.SingleAsync();
        Assert.Equal("TelemetryWarning", warningAlert.Type);
        Assert.Equal("Active", warningAlert.Status);

        connectionState.ActivatedAt = DateTime.UtcNow.AddMinutes(-6);
        await db.SaveChangesAsync();
        await service.SyncAsync(CancellationToken.None);
        var offlineAlert = await db.OperationalAlerts.SingleAsync(alert => alert.Type == "TelemetryOffline");
        Assert.Equal("TelemetryOffline", offlineAlert.Type);
        Assert.Equal("Active", offlineAlert.Status);
        Assert.Equal("Resolved", (await db.OperationalAlerts.SingleAsync(alert => alert.Type == "TelemetryWarning")).Status);

        connectionState.ActivatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await service.SyncAsync(CancellationToken.None);
        Assert.All(await db.OperationalAlerts.ToListAsync(), alert => Assert.Equal("Resolved", alert.Status));
    }

    [Fact]
    public async Task Sync_AlertsForOldPendingPaymentWithoutProviderId()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var companyId = Guid.NewGuid();
        var machine = new Machine
        {
            Id = "pending-no-provider-id",
            Name = "Máquina A",
            SerialNumber = "pending-no-provider-id",
            CompanyId = companyId
        };
        var payment = new PaymentTransaction
        {
            CompanyId = companyId,
            MachineId = machine.Id,
            Amount = 12m,
            Status = "Pending",
            CreatedAt = DateTime.UtcNow.AddMinutes(-10)
        };
        db.Companies.Add(new Company { Id = companyId, Name = "Empresa A", Cnpj = "44444444000144" });
        db.Machines.Add(machine);
        db.MachineConnectionStates.Add(new MachineConnectionState
        {
            MachineId = machine.Id,
            CompanyId = companyId,
            MonitoringEnabled = true,
            ActivatedAt = DateTime.UtcNow
        });
        db.PaymentTransactions.Add(payment);
        await db.SaveChangesAsync();

        var service = new OperationalAlertSyncService(db, NullLogger<OperationalAlertSyncService>.Instance);
        await service.SyncAsync(CancellationToken.None);

        var alert = await db.OperationalAlerts.SingleAsync();
        Assert.Equal("PaymentPending", alert.Type);
        Assert.Equal(payment.Id, alert.TransactionId);
        Assert.Contains("ainda não tem identificador confirmado pelo provedor", alert.Description, StringComparison.Ordinal);
    }
}
