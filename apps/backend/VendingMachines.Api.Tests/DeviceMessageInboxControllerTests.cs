using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using VendingMachines.Api.Controllers;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class DeviceMessageInboxControllerTests
{
    [Fact]
    public async Task Retry_OnlyRequeuesFailedMessageAndRecordsAudit()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var failed = new DeviceMessageInbox
        {
            MachineId = "machine-1", CompanyId = Guid.NewGuid(), Serial = "SERIAL-1",
            ConnectionId = "connection-1", MessageId = 8, DataJson = "{}",
            IdempotencyKey = new string('a', 64), Status = "Failed", Attempts = 8,
            LastError = "temporary database error"
        };
        var pending = new DeviceMessageInbox
        {
            MachineId = "machine-2", CompanyId = Guid.NewGuid(), Serial = "SERIAL-2",
            ConnectionId = "connection-2", MessageId = 9, DataJson = "{}",
            IdempotencyKey = new string('b', 64), Status = "Pending"
        };
        db.DeviceMessageInbox.AddRange(failed, pending);
        await db.SaveChangesAsync();
        var audit = new RecordingAuditLog();
        var controller = new DeviceMessageInboxController(db, CreateInboxService(db), audit);

        var retryResult = await controller.Retry(failed.Id, CancellationToken.None);
        var accepted = Assert.IsAssignableFrom<Microsoft.AspNetCore.Mvc.Infrastructure.IStatusCodeActionResult>(retryResult);
        Assert.Equal(202, accepted.StatusCode);
        Assert.Equal("Pending", (await db.DeviceMessageInbox.AsNoTracking().SingleAsync(row => row.Id == failed.Id)).Status);
        Assert.Equal(0, await db.DeviceMessageInbox.AsNoTracking().Where(row => row.Id == failed.Id).Select(row => row.Attempts).SingleAsync());
        Assert.Single(audit.Actions);
        Assert.Equal("telemetry.inbox.retried", audit.Actions[0]);

        Assert.IsType<ConflictObjectResult>(await controller.Retry(pending.Id, CancellationToken.None));
        Assert.IsType<NotFoundResult>(await controller.Retry(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task Retry_WhenPendingLimitIsFull_LeavesFailedMessageUnchanged()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var failed = new DeviceMessageInbox
        {
            MachineId = "failed-machine", CompanyId = Guid.NewGuid(), Serial = "SERIAL-FAILED",
            ConnectionId = "connection-failed", MessageId = 900, DataJson = "{}",
            IdempotencyKey = new string('c', 64), Status = "Failed", Attempts = 8,
            LastError = "handler error"
        };
        db.DeviceMessageInbox.Add(failed);
        db.DeviceMessageInbox.AddRange(Enumerable.Range(0, 100).Select(index => new DeviceMessageInbox
        {
            MachineId = $"pending-machine-{index}", CompanyId = Guid.NewGuid(), Serial = $"SERIAL-{index}",
            ConnectionId = "connection", MessageId = index, DataJson = "{}",
            IdempotencyKey = index.ToString("x64"), Status = "Pending"
        }));
        await db.SaveChangesAsync();

        var controller = new DeviceMessageInboxController(
            db, CreateInboxService(db, maximumPendingMessages: 100), new RecordingAuditLog());

        var result = await controller.Retry(failed.Id, CancellationToken.None);

        Assert.Equal(503, Assert.IsType<ObjectResult>(result).StatusCode);
        var unchanged = await db.DeviceMessageInbox.AsNoTracking().SingleAsync(row => row.Id == failed.Id);
        Assert.Equal("Failed", unchanged.Status);
        Assert.Equal(8, unchanged.Attempts);
    }

    [Fact]
    public void Controller_RequiresPlatformAdmin()
    {
        var authorization = typeof(DeviceMessageInboxController)
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), inherit: true)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()
            .Single();

        Assert.Equal(AppRoles.PlatformAdmin, authorization.Roles);
    }

    private sealed class RecordingAuditLog : IAuditLogService
    {
        public List<string> Actions { get; } = [];

        public Task RecordAsync(string action, string resourceType, string? resourceId = null,
            Guid? companyId = null, object? metadata = null, CancellationToken cancellationToken = default)
        {
            Actions.Add(action);
            return Task.CompletedTask;
        }
    }

    private static DeviceMessageInboxService CreateInboxService(AppDbContext db, int maximumPendingMessages = 5000)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Telemetry:MaxPendingInboundMessages"] = maximumPendingMessages.ToString()
            }).Build();
        return new DeviceMessageInboxService(db, configuration, new DeviceMessageInboxAdmissionGate());
    }
}
