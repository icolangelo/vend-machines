using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class MdbStatusPollingWorkerTests
{
    [Fact]
    public async Task StartAsyncReturnsBeforeInitialPollingDependencyCompletes()
    {
        using var scopeFactory = new BlockingScopeFactory();
        var options = Options.Create(new MdbStatusOptions { PollJitterSeconds = 0 });
        var worker = new MdbStatusPollingWorker(
            scopeFactory,
            options,
            NullLogger<MdbStatusPollingWorker>.Instance);
        var startTask = Task.Run(() => worker.StartAsync(CancellationToken.None));

        try
        {
            var completed = await Task.WhenAny(startTask, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.Same(startTask, completed);
            await startTask;
        }
        finally
        {
            scopeFactory.Release();
            await startTask.WaitAsync(TimeSpan.FromSeconds(5));
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await worker.StopAsync(stopTimeout.Token);
        }
    }

    [Fact]
    public async Task LegacyOwnerIsSkippedWithoutClaimingPollWindowOrDegradingHealth()
    {
        var connectionString = $"Data Source=mdb-status-poll-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var databaseKeeper = new SqliteConnection(connectionString);
        await databaseKeeper.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
        var companyId = Guid.NewGuid();

        await using (var db = new AppDbContext(dbOptions))
        {
            await db.Database.EnsureCreatedAsync();
            db.Companies.Add(new Company { Id = companyId, Name = "Empresa durante atualização" });
            db.Machines.Add(new Machine
            {
                Id = "machine-legacy",
                Name = "Máquina legada",
                SerialNumber = "SERIAL-LEGACY",
                NormalizedSerialNumber = "SERIAL-LEGACY",
                CompanyId = companyId
            });
            db.MachineConnectionStates.Add(new MachineConnectionState
            {
                MachineId = "machine-legacy",
                CompanyId = companyId,
                IsOnline = true,
                MonitoringEnabled = true,
                LastSeenAt = DateTime.UtcNow,
                OwnerNodeId = "legacy-node",
                OwnerConnectionId = "legacy-socket",
                CommandRoutingVersion = null
            });
            await db.SaveChangesAsync();
        }

        var mdbOptions = Options.Create(new MdbStatusOptions
        {
            PollIntervalSeconds = 30,
            PollJitterSeconds = 0,
            ResponseTimeoutSeconds = 1
        });
        var health = new BackgroundWorkerHealthRegistry();
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));
        services.AddSingleton(new TelemetryPanelHub(NullLogger<TelemetryPanelHub>.Instance));
        services.AddSingleton(serviceProvider => new TelemetryConnectionManager(
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            serviceProvider.GetRequiredService<TelemetryPanelHub>(),
            NullLogger<TelemetryConnectionManager>.Instance));
        services.AddSingleton(mdbOptions);
        services.AddScoped(serviceProvider => new MdbStatusRequestService(
            serviceProvider.GetRequiredService<AppDbContext>(),
            serviceProvider.GetRequiredService<TelemetryConnectionManager>(),
            mdbOptions,
            NullLogger<MdbStatusRequestService>.Instance));
        await using var provider = services.BuildServiceProvider();

        var worker = new MdbStatusPollingWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            mdbOptions,
            NullLogger<MdbStatusPollingWorker>.Instance,
            health);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            BackgroundWorkerHealthSnapshot? snapshot;
            do
            {
                snapshot = health.GetSnapshots().Single(item => item.Name == BackgroundWorkerNames.MdbStatusPolling);
                if (snapshot.LastHeartbeatAt is not null) break;
                await Task.Delay(20);
            } while (DateTime.UtcNow < deadline);

            Assert.NotNull(snapshot);
            Assert.Equal("Healthy", snapshot.Status);
            Assert.Equal(0, snapshot.ConsecutiveFailures);

            Assert.Null(snapshot.LastSuccessAt);

            await using var verifyDb = new AppDbContext(dbOptions);
            var state = await verifyDb.MachineConnectionStates.SingleAsync();
            Assert.True(state.IsOnline);
            Assert.Null(state.MdbStatusRequestAt);
            Assert.Empty(await verifyDb.TelemetryCommandDispatches.ToListAsync());
        }
        finally
        {
            await worker.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        }
    }

    private sealed class BlockingScopeFactory : IServiceScopeFactory, IDisposable
    {
        private readonly ManualResetEventSlim _release = new(false);

        public void Release() => _release.Set();

        public IServiceScope CreateScope()
        {
            if (!_release.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("O fixture não liberou o scope de polling MDB.");
            }

            throw new InvalidOperationException("Falha sintética após liberar o scope.");
        }

        public void Dispose() => _release.Dispose();
    }
}
