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

        var resolved = await db.OperationalAlerts.AsNoTracking().ToListAsync();
        Assert.Equal(5, resolved.Count);
        Assert.All(resolved, alert =>
        {
            Assert.Equal("Resolved", alert.Status);
            Assert.NotNull(alert.ResolvedAt);
        });
    }
}
