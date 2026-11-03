using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class DeviceCredentialReadinessTests
{
    [Fact]
    public async Task StartupGateRejectsAnActiveMachineWithoutCredential()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.Machines.Add(CreateMachine("active-without-credential", isActive: true));
        await db.SaveChangesAsync();

        Assert.True(DeviceCredentialReadiness.HasActiveMachinesWithoutValidCredential(db.Machines));
    }

    [Fact]
    public async Task StartupGateIgnoresInactiveMachinesButRejectsRevokedActiveCredential()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.Machines.AddRange(
            CreateMachine("inactive-without-credential", isActive: false),
            CreateMachine("active-with-credential", isActive: true, DeviceCredentialService.Hash("synthetic-credential")));
        await db.SaveChangesAsync();

        Assert.False(DeviceCredentialReadiness.HasActiveMachinesWithoutValidCredential(db.Machines));

        var activeMachine = await db.Machines.SingleAsync(machine => machine.Id == "active-with-credential");
        activeMachine.TelemetryCredentialRevokedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        Assert.True(DeviceCredentialReadiness.HasActiveMachinesWithoutValidCredential(db.Machines));
    }

    private static Machine CreateMachine(string id, bool isActive, string? credentialHash = null) => new()
    {
        Id = id,
        Name = id,
        SerialNumber = id,
        NormalizedSerialNumber = id.ToUpperInvariant(),
        IsActive = isActive,
        TelemetryCredentialHash = credentialHash
    };
}
