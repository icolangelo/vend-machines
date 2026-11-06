using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class TelemetryConnectionLeaseWorkerTests
{
    [Fact]
    public async Task WorkerRenewsActiveConnectionLeasesAndExpiresThemOnShutdown()
    {
        var connectionString = $"Data Source=telemetry-connection-lease-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var databaseKeeper = new SqliteConnection(connectionString);
        await databaseKeeper.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
        var company = new Company { Name = "Empresa" };
        var machine = new Machine { Id = "lease-machine", Name = "Máquina", SerialNumber = "LEASE-1", CompanyId = company.Id };
        var orphanMachine = new Machine { Id = "orphan-machine", Name = "Máquina órfã", SerialNumber = "LEASE-2", CompanyId = company.Id };
        await using (var db = new AppDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.Companies.Add(company);
            db.Machines.AddRange(machine, orphanMachine);
            db.MachineConnectionStates.Add(new MachineConnectionState
            {
                MachineId = machine.Id,
                CompanyId = company.Id,
                IsOnline = true,
                ConnectionId = "socket-1",
                OwnerNodeId = "node-under-test",
                OwnerConnectionId = "socket-1",
                OwnerLeaseExpiresAt = DateTime.UtcNow.AddDays(-1)
            });
            db.MachineConnectionStates.Add(new MachineConnectionState
            {
                MachineId = orphanMachine.Id,
                CompanyId = company.Id,
                IsOnline = true,
                ConnectionId = "socket-orphaned",
                OwnerNodeId = "node-under-test",
                OwnerConnectionId = "socket-orphaned",
                OwnerLeaseExpiresAt = DateTime.UtcNow.AddDays(-1)
            });
            await db.SaveChangesAsync();
        }

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(builder => builder.UseSqlite(connectionString));
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();
        var health = new BackgroundWorkerHealthRegistry();
        var worker = new TelemetryConnectionLeaseWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            "node-under-test",
            () => new Dictionary<string, string> { [machine.Id] = "socket-1" },
            NullLogger<TelemetryConnectionLeaseWorker>.Instance,
            health);

        await worker.StartAsync(CancellationToken.None);
        var leaseDeadline = DateTime.UtcNow.AddSeconds(3);
        DateTime? leaseExpiresAt = null;
        while (DateTime.UtcNow < leaseDeadline)
        {
            await using var db = new AppDbContext(options);
            leaseExpiresAt = await db.MachineConnectionStates.AsNoTracking()
                .Where(state => state.MachineId == machine.Id)
                .Select(state => state.OwnerLeaseExpiresAt)
                .SingleAsync();
            if (leaseExpiresAt > DateTime.UtcNow) break;
            await Task.Delay(20);
        }

        Assert.True(leaseExpiresAt > DateTime.UtcNow);
        await using (var db = new AppDbContext(options))
        {
            var orphanLease = await db.MachineConnectionStates.AsNoTracking()
                .Where(state => state.MachineId == orphanMachine.Id)
                .Select(state => state.OwnerLeaseExpiresAt)
                .SingleAsync();
            Assert.True(orphanLease <= DateTime.UtcNow);
        }
        Assert.Equal(BackgroundWorkerStatus.Healthy, health.GetSnapshots()
            .Single(item => item.Name == BackgroundWorkerNames.TelemetryConnectionLease).Status);

        await worker.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        await using (var db = new AppDbContext(options))
        {
            leaseExpiresAt = await db.MachineConnectionStates.AsNoTracking()
                .Where(state => state.MachineId == machine.Id)
                .Select(state => state.OwnerLeaseExpiresAt)
                .SingleAsync();
        }

        Assert.True(leaseExpiresAt <= DateTime.UtcNow);
        Assert.Equal("Stopped", health.GetSnapshots()
            .Single(item => item.Name == BackgroundWorkerNames.TelemetryConnectionLease).Status);
    }
}
