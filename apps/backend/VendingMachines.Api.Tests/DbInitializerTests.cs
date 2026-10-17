using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class DbInitializerTests
{
    [Fact]
    public async Task DemoDataIsAllowedOnlyForExplicitSqliteDevelopmentSeed()
    {
        await using var sqliteConnection = new SqliteConnection("Data Source=:memory:");
        var sqliteOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(sqliteConnection)
            .Options;
        await using var sqliteDb = new AppDbContext(sqliteOptions);

        Assert.True(DbInitializer.CanSeedDemoData(sqliteDb.Database, requested: true));
        Assert.False(DbInitializer.CanSeedDemoData(sqliteDb.Database, requested: false));

        var postgresOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=vendmachines_never_connect;Username=unused;Password=unused")
            .Options;
        await using var postgresDb = new AppDbContext(postgresOptions);

        Assert.False(DbInitializer.CanSeedDemoData(postgresDb.Database, requested: true));
    }

    [Fact]
    public async Task ExplicitSqliteSeedStillCreatesLocalDemoData()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);

        DbInitializer.Initialize(db, seedDemoData: true);

        Assert.Contains(await db.Companies.Select(company => company.Name).ToListAsync(),
            name => name == "ACME Machines LTDA");
        Assert.True(await db.Machines.CountAsync() > 0);
    }

    [Fact]
    public async Task ExistingSqliteConnectionStateAddsOwnerNodeWithoutChangingLiveState()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var company = new Company
        {
            Name = "Empresa local de teste",
            Cnpj = "12.345.678/0001-95",
            NormalizedCnpj = "12345678000195",
            ActivationStatus = CompanyActivationStatuses.Active
        };
        var machine = new Machine
        {
            Id = "machine-1",
            Name = "Máquina legada",
            SerialNumber = "SERIAL-1",
            NormalizedSerialNumber = "SERIAL-1",
            CompanyId = company.Id,
            Company = company
        };
        db.Companies.Add(company);
        db.Machines.Add(machine);
        db.MachineConnectionStates.Add(new MachineConnectionState
        {
            MachineId = machine.Id,
            CompanyId = company.Id,
            IsOnline = true,
            ConnectionId = "legacy-socket"
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"MachineConnectionStates\" DROP COLUMN \"OwnerConnectionId\"");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"MachineConnectionStates\" DROP COLUMN \"OwnerNodeId\"");
        await db.Database.ExecuteSqlRawAsync("DROP TABLE \"TelemetryCommandDispatches\"");

        DbInitializer.Initialize(db, seedDemoData: false);

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var schema = connection.CreateCommand())
        {
            schema.CommandText = "PRAGMA table_info(\"MachineConnectionStates\")";
            await using var reader = await schema.ExecuteReaderAsync();
            while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
        }
        Assert.Contains("OwnerNodeId", columns);
        Assert.Contains("OwnerConnectionId", columns);

        await using var state = connection.CreateCommand();
        state.CommandText = "SELECT \"IsOnline\", \"ConnectionId\", \"OwnerNodeId\", \"OwnerConnectionId\" FROM \"MachineConnectionStates\" WHERE \"MachineId\" = 'machine-1'";
        await using var stateReader = await state.ExecuteReaderAsync();
        Assert.True(await stateReader.ReadAsync());
        Assert.Equal(1, stateReader.GetInt32(0));
        Assert.Equal("legacy-socket", stateReader.GetString(1));
        Assert.True(await stateReader.IsDBNullAsync(2));
        Assert.True(await stateReader.IsDBNullAsync(3));

        await using var dispatchTable = connection.CreateCommand();
        dispatchTable.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = 'TelemetryCommandDispatches'";
        Assert.Equal(1L, (long)(await dispatchTable.ExecuteScalarAsync())!);
    }
}
