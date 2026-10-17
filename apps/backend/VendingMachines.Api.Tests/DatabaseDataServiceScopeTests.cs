using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class DatabaseDataServiceScopeTests
{
    [Fact]
    public async Task GetMachines_ReturnsOnlyMachinesFromRequestedCompany()
    {
        await using var scenario = await CreateScenarioAsync();
        var companyA = Guid.NewGuid();
        var companyB = Guid.NewGuid();

        scenario.Db.Companies.AddRange(
            new Company { Id = companyA, Name = "Empresa A" },
            new Company { Id = companyB, Name = "Empresa B" });
        await scenario.Db.SaveChangesAsync();

        scenario.Db.Machines.AddRange(
            new Machine { Id = "A-1", Name = "A", CompanyId = companyA, SerialNumber = "A-1" },
            new Machine { Id = "B-1", Name = "B", CompanyId = companyB, SerialNumber = "B-1" });
        await scenario.Db.SaveChangesAsync();

        var result = new DatabaseDataService(scenario.Db).GetMachines(companyA);

        var machine = Assert.Single(result);
        Assert.Equal("A-1", machine.Id);
    }

    [Fact]
    public async Task GetMachinesAndLocationsIgnoreOperationalRowsAssignedToAnotherCompany()
    {
        await using var scenario = await CreateScenarioAsync();
        var companyA = new Company { Id = Guid.NewGuid(), Name = "Empresa A" };
        var companyB = new Company { Id = Guid.NewGuid(), Name = "Empresa B" };
        var locationA = new Location { Id = Guid.NewGuid(), CompanyId = companyA.Id, Name = "Local A" };
        var machineA = new Machine
        {
            Id = "A-1", Name = "Máquina A", CompanyId = companyA.Id, SerialNumber = "A-1",
            LocationId = locationA.Id
        };
        var productTypeB = new ProductType { Id = "type-b", Name = "Tipo B", CompanyId = companyB.Id };
        var productB = new FullProduct
        {
            Id = "product-b", Name = "Produto B", Code = "B", TypeId = productTypeB.Id, CompanyId = companyB.Id
        };
        var transactionB = new PaymentTransaction
        {
            Id = Guid.NewGuid(), CompanyId = companyB.Id, MachineId = machineA.Id, Amount = 900m, Status = "Approved"
        };
        var sessionB = new MachineSession
        {
            Id = Guid.NewGuid(), CompanyId = companyB.Id, MachineId = machineA.Id,
            TransactionId = transactionB.Id, State = MachineSessionStates.Completed
        };
        scenario.Db.Companies.AddRange(companyA, companyB);
        scenario.Db.Locations.Add(locationA);
        scenario.Db.Machines.Add(machineA);
        scenario.Db.ProductTypes.Add(productTypeB);
        scenario.Db.Products.Add(productB);
        scenario.Db.PaymentTransactions.Add(transactionB);
        scenario.Db.MachineSessions.Add(sessionB);
        scenario.Db.SaleRecords.Add(new SaleRecord
        {
            CompanyId = companyB.Id, MachineId = machineA.Id, SessionId = sessionB.Id,
            TransactionId = transactionB.Id, ItemNumber = 1, ProductId = productB.Id,
            Quantity = 90, GrossAmount = 900m, SoldAt = DateTime.UtcNow
        });
        scenario.Db.MachineConnectionStates.Add(new MachineConnectionState
        {
            MachineId = machineA.Id, CompanyId = companyB.Id, MonitoringEnabled = true,
            IsOnline = true, ConnectedAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow
        });
        scenario.Db.MachineProductSlots.Add(new MachineProductSlot
        {
            CompanyId = companyB.Id, MachineId = machineA.Id, ItemNumber = 1,
            ProductId = productB.Id, Capacity = 100, CurrentStock = 80, LowStockThreshold = 10
        });
        await scenario.Db.SaveChangesAsync();

        var service = new DatabaseDataService(scenario.Db);
        var machine = Assert.Single(service.GetMachines(companyA.Id));
        var location = Assert.Single(service.GetLocations(companyA.Id));

        Assert.Equal(0m, machine.Revenue30d);
        Assert.Equal(0, machine.TotalSales30d);
        Assert.Equal("unmonitored", machine.Status);
        Assert.False(machine.InventoryConfigured);
        Assert.False(machine.LowStock);
        Assert.Equal(0m, location.Revenue30d);
        Assert.Equal(0, location.TotalSales30d);
    }

    [Fact]
    public async Task GetMachinesFlagsOnlyMachinesBelowTheirConfiguredThirtyDaySalesTarget()
    {
        await using var scenario = await CreateScenarioAsync();
        var companyId = Guid.NewGuid();
        var belowTarget = new Machine
        {
            Id = "TARGET-LOW", Name = "Abaixo da meta", CompanyId = companyId,
            SerialNumber = "TARGET-LOW", MinimumSalesTarget30d = 5
        };
        var meetingTarget = new Machine
        {
            Id = "TARGET-MET", Name = "Na meta", CompanyId = companyId,
            SerialNumber = "TARGET-MET", MinimumSalesTarget30d = 3
        };
        var noTarget = new Machine
        {
            Id = "TARGET-NONE", Name = "Sem meta", CompanyId = companyId,
            SerialNumber = "TARGET-NONE"
        };
        scenario.Db.Companies.Add(new Company { Id = companyId, Name = "Empresa" });
        scenario.Db.Machines.AddRange(belowTarget, meetingTarget, noTarget);

        foreach (var machine in new[] { belowTarget, meetingTarget })
        {
            var session = new MachineSession
            {
                Id = Guid.NewGuid(), CompanyId = companyId, MachineId = machine.Id,
                State = MachineSessionStates.Completed
            };
            var transaction = new PaymentTransaction
            {
                Id = Guid.NewGuid(), CompanyId = companyId, MachineId = machine.Id,
                Amount = 10m, Status = "Approved"
            };
            scenario.Db.MachineSessions.Add(session);
            scenario.Db.PaymentTransactions.Add(transaction);
            scenario.Db.SaleRecords.Add(new SaleRecord
            {
                CompanyId = companyId, MachineId = machine.Id, SessionId = session.Id,
                TransactionId = transaction.Id, ItemNumber = 1, Quantity = machine == belowTarget ? 4 : 3,
                GrossAmount = 10m, SoldAt = DateTime.UtcNow.AddDays(-1)
            });
        }

        await scenario.Db.SaveChangesAsync();

        var results = new DatabaseDataService(scenario.Db).GetMachines(companyId).ToDictionary(machine => machine.Id);

        Assert.True(results[belowTarget.Id].BelowSalesTarget);
        Assert.Equal(4, results[belowTarget.Id].TotalSales30d);
        Assert.False(results[meetingTarget.Id].BelowSalesTarget);
        Assert.Equal(3, results[meetingTarget.Id].TotalSales30d);
        Assert.Null(results[noTarget.Id].MinimumSalesTarget30d);
        Assert.False(results[noTarget.Id].BelowSalesTarget);
    }

    [Fact]
    public async Task GetProducts_IncludesGlobalProductsAndExcludesAnotherCompany()
    {
        await using var scenario = await CreateScenarioAsync();
        var companyA = Guid.NewGuid();
        var companyB = Guid.NewGuid();

        scenario.Db.Companies.AddRange(
            new Company { Id = companyA, Name = "Empresa A" },
            new Company { Id = companyB, Name = "Empresa B" });
        scenario.Db.ProductTypes.Add(new ProductType { Id = "type", Name = "Tipo" });
        await scenario.Db.SaveChangesAsync();

        scenario.Db.Products.AddRange(
            new FullProduct { Id = "global", Name = "Global", Code = "G", TypeId = "type", CompanyId = null },
            new FullProduct { Id = "a", Name = "A", Code = "A", TypeId = "type", CompanyId = companyA },
            new FullProduct { Id = "b", Name = "B", Code = "B", TypeId = "type", CompanyId = companyB });
        await scenario.Db.SaveChangesAsync();

        var result = new DatabaseDataService(scenario.Db).GetProducts(companyA);

        Assert.Contains(result, product => product.Id == "global");
        Assert.Contains(result, product => product.Id == "a");
        Assert.DoesNotContain(result, product => product.Id == "b");
    }

    [Fact]
    public async Task ProductRankings_UseDeliveredSalesAndStayWithinCompanyScope()
    {
        await using var scenario = await CreateScenarioAsync();
        var companyA = Guid.NewGuid();
        var companyB = Guid.NewGuid();
        var machineA = new Machine { Id = "A-1", Name = "Máquina A", CompanyId = companyA, SerialNumber = "A-1" };
        var machineB = new Machine { Id = "B-1", Name = "Máquina B", CompanyId = companyB, SerialNumber = "B-1" };
        var productType = new ProductType { Id = "drink", Name = "Bebida" };
        scenario.Db.Companies.AddRange(
            new Company { Id = companyA, Name = "Empresa A" },
            new Company { Id = companyB, Name = "Empresa B" });
        scenario.Db.Machines.AddRange(machineA, machineB);
        scenario.Db.ProductTypes.Add(productType);
        scenario.Db.Products.AddRange(
            new FullProduct { Id = "coffee", Name = "Café", Code = "C1", TypeId = productType.Id, CompanyId = companyA },
            new FullProduct { Id = "tea", Name = "Chá", Code = "T1", TypeId = productType.Id, CompanyId = companyA },
            new FullProduct { Id = "soda", Name = "Refrigerante", Code = "S1", TypeId = productType.Id, CompanyId = companyB });
        var sessionA = new MachineSession { Id = Guid.NewGuid(), CompanyId = companyA, MachineId = machineA.Id, State = MachineSessionStates.Completed };
        var sessionB = new MachineSession { Id = Guid.NewGuid(), CompanyId = companyB, MachineId = machineB.Id, State = MachineSessionStates.Completed };
        var transactionA = new PaymentTransaction { Id = Guid.NewGuid(), CompanyId = companyA, MachineId = machineA.Id, Amount = 8m, Status = "Approved" };
        var transactionB = new PaymentTransaction { Id = Guid.NewGuid(), CompanyId = companyB, MachineId = machineB.Id, Amount = 100m, Status = "Approved" };
        scenario.Db.MachineSessions.AddRange(sessionA, sessionB);
        scenario.Db.PaymentTransactions.AddRange(transactionA, transactionB);
        scenario.Db.MachineProductSlots.AddRange(
            new MachineProductSlot { CompanyId = companyA, MachineId = machineA.Id, ItemNumber = 1, ProductId = "coffee", Capacity = 20 },
            new MachineProductSlot { CompanyId = companyA, MachineId = machineA.Id, ItemNumber = 2, ProductId = "tea", Capacity = 20 },
            new MachineProductSlot { CompanyId = companyB, MachineId = machineB.Id, ItemNumber = 1, ProductId = "soda", Capacity = 20 });
        scenario.Db.SaleRecords.AddRange(
            new SaleRecord
            {
                CompanyId = companyA, MachineId = machineA.Id, SessionId = sessionA.Id,
                TransactionId = transactionA.Id, ItemNumber = 1, ProductId = "coffee",
                ProductNameSnapshot = "Café", Quantity = 2, GrossAmount = 8m, RefundedAmount = 2m, SoldAt = DateTime.UtcNow
            },
            new SaleRecord
            {
                CompanyId = companyB, MachineId = machineB.Id, SessionId = sessionB.Id,
                TransactionId = transactionB.Id, ItemNumber = 1, ProductId = "soda",
                ProductNameSnapshot = "Refrigerante", Quantity = 1, GrossAmount = 100m, SoldAt = DateTime.UtcNow
            });
        await scenario.Db.SaveChangesAsync();

        var service = new DatabaseDataService(scenario.Db);
        var top = Assert.Single(service.GetTopProducts(companyA));
        var bottom = service.GetBottomProducts(companyA);
        var stats = service.GetDashboardStats(companyA);

        Assert.Equal("Café", top.Name);
        Assert.Equal(2, top.TotalSold);
        Assert.Equal(6m, top.Revenue);
        Assert.Equal("Chá", bottom.First().Name);
        Assert.Equal(0, bottom.First().TotalSold);
        Assert.DoesNotContain(bottom, product => product.Name == "Refrigerante");
        Assert.Equal(6m, stats.TotalRevenue30d);
        Assert.Equal(2, stats.TotalSales30d);
    }

    [Fact]
    public async Task Dashboard_DerivesMachineStatusAndLowStockFromObservedTelemetryAndSlotThreshold()
    {
        await using var scenario = await CreateScenarioAsync();
        var companyId = Guid.NewGuid();
        var machine = new Machine { Id = "A-telemetry", Name = "Máquina monitorada", CompanyId = companyId, SerialNumber = "A-telemetry" };
        scenario.Db.Companies.Add(new Company { Id = companyId, Name = "Empresa A" });
        scenario.Db.Machines.Add(machine);
        scenario.Db.ProductTypes.Add(new ProductType { Id = "coffee-type", Name = "Bebida" });
        scenario.Db.Products.Add(new FullProduct
        {
            Id = "coffee",
            Name = "Café",
            Code = "COF",
            TypeId = "coffee-type",
            CompanyId = companyId
        });
        scenario.Db.MachineConnectionStates.Add(new MachineConnectionState
        {
            MachineId = machine.Id,
            CompanyId = companyId,
            MonitoringEnabled = true,
            IsOnline = false,
            LastSeenAt = DateTime.UtcNow.AddMinutes(-3)
        });
        scenario.Db.MachineProductSlots.Add(new MachineProductSlot
        {
            CompanyId = companyId,
            MachineId = machine.Id,
            ItemNumber = 1,
            ProductId = "coffee",
            Capacity = 20,
            CurrentStock = 4,
            LowStockThreshold = 5
        });
        await scenario.Db.SaveChangesAsync();

        var service = new DatabaseDataService(scenario.Db);
        var observedMachine = Assert.Single(service.GetMachines(companyId));
        var dashboard = service.GetDashboardStats(companyId);

        Assert.Equal("warning", observedMachine.Status);
        Assert.True(observedMachine.InventoryConfigured);
        Assert.True(observedMachine.LowStock);
        Assert.Equal(20, observedMachine.StockLevel);
        Assert.Equal(1, dashboard.WarningMachines);
        Assert.Equal(0, dashboard.OfflineMachines);
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
