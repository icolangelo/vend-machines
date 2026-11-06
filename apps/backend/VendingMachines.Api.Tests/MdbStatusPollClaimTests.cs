using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class MdbStatusPollClaimTests
{
    [Fact]
    public async Task DueStateSelectionSkipsPersistedConnectionsWithExpiredLastSeen()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var companyId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.Companies.Add(new Company { Id = companyId, Name = "Empresa de teste" });
        db.Machines.AddRange(
            new Machine
            {
                Id = "machine-fresh",
                Name = "Máquina recente",
                SerialNumber = "SERIAL-FRESH",
                NormalizedSerialNumber = "SERIAL-FRESH",
                CompanyId = companyId
            },
            new Machine
            {
                Id = "machine-stale",
                Name = "Máquina sem contato recente",
                SerialNumber = "SERIAL-STALE",
                NormalizedSerialNumber = "SERIAL-STALE",
                CompanyId = companyId
            },
            new Machine
            {
                Id = "machine-legacy-owner",
                Name = "Máquina com proprietário legado",
                SerialNumber = "SERIAL-LEGACY",
                NormalizedSerialNumber = "SERIAL-LEGACY",
                CompanyId = companyId
            });
        db.MachineConnectionStates.AddRange(
            new MachineConnectionState
            {
                MachineId = "machine-fresh",
                CompanyId = companyId,
                IsOnline = true,
                MonitoringEnabled = true,
                LastSeenAt = now.AddSeconds(-30),
                ConnectionId = "current-socket",
                OwnerNodeId = "current-node",
                OwnerConnectionId = "current-socket",
                CommandRoutingVersion = TelemetryConnectionManager.CurrentCommandRoutingVersion,
                OwnerLeaseExpiresAt = now.AddMinutes(1)
            },
            new MachineConnectionState
            {
                MachineId = "machine-stale",
                CompanyId = companyId,
                IsOnline = true,
                MonitoringEnabled = true,
                LastSeenAt = now.AddSeconds(-91)
            },
            new MachineConnectionState
            {
                MachineId = "machine-legacy-owner",
                CompanyId = companyId,
                IsOnline = true,
                MonitoringEnabled = true,
                LastSeenAt = now.AddSeconds(-10),
                ConnectionId = "legacy-socket"
            });
        await db.SaveChangesAsync();

        var dueMachines = await MdbStatusPollClaim.GetDueStates(
                db,
                now.AddSeconds(-30),
                now.AddSeconds(-90),
                now)
            .Select(state => state.MachineId)
            .ToListAsync();

        Assert.Equal(new[] { "machine-fresh" }, dueMachines);
    }

    [Fact]
    public async Task OnlyOnePollingWorkerClaimsEachMachineWindow()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var companyId = Guid.NewGuid();
        db.Companies.Add(new Company { Id = companyId, Name = "Empresa de teste" });
        db.Machines.Add(new Machine
        {
            Id = "machine-1",
            Name = "Máquina de teste",
            SerialNumber = "SERIAL-1",
            NormalizedSerialNumber = "SERIAL-1",
            CompanyId = companyId
        });
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
            MachineId = "machine-1",
            CompanyId = companyId,
            IsOnline = true,
            MonitoringEnabled = true,
            ConnectionId = "current-socket",
            OwnerNodeId = "current-node",
            OwnerConnectionId = "current-socket",
            CommandRoutingVersion = TelemetryConnectionManager.CurrentCommandRoutingVersion,
            OwnerLeaseExpiresAt = DateTime.UtcNow.AddMinutes(1)
        });
        db.MachineConnectionStates.Add(new MachineConnectionState
        {
            MachineId = "machine-legacy",
            CompanyId = companyId,
            IsOnline = true,
            MonitoringEnabled = true,
            ConnectionId = "legacy-socket"
        });
        await db.SaveChangesAsync();

        var firstRequestAt = DateTime.UtcNow;
        Assert.True(await MdbStatusPollClaim.TryClaimAsync(
            db, "machine-1", companyId, firstRequestAt, 30, CancellationToken.None));
        Assert.False(await MdbStatusPollClaim.TryClaimAsync(
            db, "machine-1", companyId, firstRequestAt.AddSeconds(1), 30, CancellationToken.None));
        Assert.False(await MdbStatusPollClaim.TryClaimAsync(
            db, "machine-legacy", companyId, firstRequestAt, 30, CancellationToken.None));

        db.ChangeTracker.Clear();
        var connectionState = await db.MachineConnectionStates.SingleAsync(state => state.MachineId == "machine-1");
        Assert.Equal(firstRequestAt, connectionState.MdbStatusRequestAt);

        Assert.True(await MdbStatusPollClaim.TryClaimAsync(
            db, "machine-1", companyId, firstRequestAt.AddSeconds(30), 30, CancellationToken.None));
    }

    [Fact]
    public async Task Postgres_AllowsOnlyOneConcurrentClaimAcrossWorkers()
    {
        var configuredConnection = Environment.GetEnvironmentVariable("VENDMACHINES_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(configuredConnection))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("VENDMACHINES_REQUIRE_TEST_POSTGRES"), "true",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("O teste concorrente de polling MDB exige VENDMACHINES_TEST_POSTGRES no CI.");
            return;
        }

        var databaseName = $"vendmachines_mdb_poll_claim_{Guid.NewGuid():N}";
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
            await using (var setup = new AppDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                setup.Companies.Add(new Company { Id = companyId, Name = "Empresa sintética" });
                setup.Machines.Add(new Machine
                {
                    Id = "machine-1",
                    Name = "Máquina sintética",
                    SerialNumber = "SERIAL-1",
                    NormalizedSerialNumber = "SERIAL-1",
                    CompanyId = companyId
                });
                setup.MachineConnectionStates.Add(new MachineConnectionState
                {
                    MachineId = "machine-1",
                    CompanyId = companyId,
                    IsOnline = true,
                    MonitoringEnabled = true,
                    ConnectionId = "current-socket",
                    OwnerNodeId = "current-node",
                    OwnerConnectionId = "current-socket",
                    CommandRoutingVersion = TelemetryConnectionManager.CurrentCommandRoutingVersion,
                    OwnerLeaseExpiresAt = DateTime.UtcNow.AddMinutes(2)
                });
                await setup.SaveChangesAsync();
            }

            var requestedAt = DateTime.UtcNow;
            async Task<bool> ClaimAsync()
            {
                await using var db = new AppDbContext(options);
                return await MdbStatusPollClaim.TryClaimAsync(
                    db, "machine-1", companyId, requestedAt, 30, CancellationToken.None);
            }

            var claims = await Task.WhenAll(ClaimAsync(), ClaimAsync());
            Assert.Single(claims, claimed => claimed);
            Assert.Single(claims, claimed => !claimed);

            await using var verify = new AppDbContext(options);
            var state = await verify.MachineConnectionStates.SingleAsync();
            Assert.Equal(requestedAt.Ticks / 10, state.MdbStatusRequestAt!.Value.Ticks / 10);
        }
        finally
        {
            await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
            await admin.OpenAsync();
            await using var dropDatabase = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin);
            await dropDatabase.ExecuteNonQueryAsync();
        }
    }
}
