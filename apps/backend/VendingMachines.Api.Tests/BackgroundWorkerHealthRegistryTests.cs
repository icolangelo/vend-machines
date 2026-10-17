using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using VendingMachines.Api.Controllers;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class BackgroundWorkerHealthRegistryTests
{
    [Fact]
    public void RegistryReportsStartupSuccessFailureStalenessAndShutdownWithoutExceptionMessages()
    {
        var registry = new BackgroundWorkerHealthRegistry();
        Assert.Equal(10, registry.GetSnapshots().Count);
        var initial = Assert.Single(registry.GetSnapshots(), worker => worker.Name == BackgroundWorkerNames.Telemetry);
        Assert.Equal("NotStarted", initial.Status);

        registry.ReportStarted(BackgroundWorkerNames.Telemetry);
        registry.ReportSuccess(BackgroundWorkerNames.Telemetry);
        var healthy = Assert.Single(registry.GetSnapshots(), worker => worker.Name == BackgroundWorkerNames.Telemetry);
        Assert.Equal("Healthy", healthy.Status);
        Assert.NotNull(healthy.LastSuccessAt);

        registry.ReportFailure(BackgroundWorkerNames.Telemetry, new InvalidOperationException("secret token must not leak"));
        var degraded = Assert.Single(registry.GetSnapshots(), worker => worker.Name == BackgroundWorkerNames.Telemetry);
        Assert.Equal("Degraded", degraded.Status);
        Assert.Equal(1, degraded.ConsecutiveFailures);
        Assert.Equal(nameof(InvalidOperationException), degraded.LastErrorType);
        Assert.DoesNotContain("secret", degraded.LastErrorType, StringComparison.OrdinalIgnoreCase);

        var stale = Assert.Single(registry.GetSnapshots(DateTime.UtcNow.AddMinutes(3)),
            worker => worker.Name == BackgroundWorkerNames.Telemetry);
        Assert.Equal("Stale", stale.Status);

        registry.ReportStopped(BackgroundWorkerNames.Telemetry);
        var stopped = Assert.Single(registry.GetSnapshots(), worker => worker.Name == BackgroundWorkerNames.Telemetry);
        Assert.Equal("Stopped", stopped.Status);
    }

    [Fact]
    public void WorkerHealthEndpointIsRestrictedToPlatformAdministrators()
    {
        var authorize = Assert.Single(typeof(WorkerHealthController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>());

        Assert.Equal(AppRoles.PlatformAdmin, authorize.Roles);
    }

    [Fact]
    public async Task WorkerHealthEndpointReturnsQueueCountsWithoutExposingPayloads()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var company = new Company { Id = Guid.NewGuid(), Name = "Empresa", Cnpj = "11111111000111" };
        var machine = new Machine { Id = "machine-a", Name = "Máquina A", SerialNumber = "A", CompanyId = company.Id };
        db.Companies.Add(company);
        db.Machines.Add(machine);
        db.MachineConnectionStates.Add(new MachineConnectionState
        {
            MachineId = machine.Id,
            CompanyId = company.Id,
            MonitoringEnabled = true,
            IsOnline = true,
            LastSeenAt = DateTime.UtcNow.AddMinutes(-2),
            OwnerNodeId = null
        });
        db.DeviceMessageInbox.Add(new DeviceMessageInbox
        {
            IdempotencyKey = "device-key",
            MachineId = machine.Id,
            CompanyId = company.Id,
            Serial = "A",
            ConnectionId = "connection-a",
            MessageId = 4,
            DataJson = "secret-payload",
            Status = "Pending",
            ReceivedAt = DateTime.UtcNow.AddMinutes(-1)
        });
        db.WebhookInboxMessages.Add(new WebhookInboxMessage
        {
            EventKey = "webhook-key",
            PaymentId = "payment-1",
            EventType = "payment",
            EventAction = "payment.updated",
            Format = "webhook",
            AuthenticationMode = "signature",
            Status = "Retry",
            LastError = "private provider response",
            ReceivedAt = DateTime.UtcNow.AddMinutes(-2)
        });
        db.TelemetryCommandDispatches.AddRange(
            new TelemetryCommandDispatch
            {
                CompanyId = company.Id,
                MachineId = machine.Id,
                OwnerNodeId = "owner-node",
                Operation = "Command",
                PayloadJson = "secret-command-payload",
                Status = "Pending",
                CreatedAt = DateTime.UtcNow.AddMinutes(-4)
            },
            new TelemetryCommandDispatch
            {
                CompanyId = company.Id,
                MachineId = machine.Id,
                OwnerNodeId = "owner-node",
                Operation = "MdbStatus",
                PayloadJson = "secret-status-payload",
                Status = "Processing",
                ProcessingStartedAt = DateTime.UtcNow.AddMinutes(-5)
            });
        db.PaymentTransactions.Add(new PaymentTransaction
        {
            Id = Guid.NewGuid(),
            CompanyId = company.Id,
            MachineId = machine.Id,
            Status = "Pending",
            CreatedAt = DateTime.UtcNow.AddMinutes(-3)
        });
        await db.SaveChangesAsync();

        var controller = new WorkerHealthController(new BackgroundWorkerHealthRegistry(), db);
        var response = Assert.IsType<OkObjectResult>(await controller.Get(CancellationToken.None));
        var payload = JsonSerializer.SerializeToElement(response.Value);

        Assert.True(payload.GetProperty("hasUnhealthyWorkers").GetBoolean());
        Assert.True(payload.GetProperty("queueMetrics").GetProperty("available").GetBoolean());
        Assert.Equal(1, payload.GetProperty("queueMetrics").GetProperty("machineConnections").GetProperty("monitored").GetInt32());
        Assert.Equal(1, payload.GetProperty("queueMetrics").GetProperty("machineConnections").GetProperty("online").GetInt32());
        Assert.Equal(1, payload.GetProperty("queueMetrics").GetProperty("machineConnections").GetProperty("staleOnline").GetInt32());
        Assert.Equal(1, payload.GetProperty("queueMetrics").GetProperty("machineConnections").GetProperty("onlineWithoutOwner").GetInt32());
        Assert.Equal(1, payload.GetProperty("queueMetrics").GetProperty("deviceMessageInbox").GetProperty("Pending").GetInt32());
        Assert.Equal(1, payload.GetProperty("queueMetrics").GetProperty("telemetryCommandDispatches").GetProperty("Pending").GetInt32());
        Assert.Equal(1, payload.GetProperty("queueMetrics").GetProperty("telemetryCommandDispatches").GetProperty("Processing").GetInt32());
        Assert.Equal(1, payload.GetProperty("queueMetrics").GetProperty("webhookInbox").GetProperty("Retry").GetInt32());
        Assert.Equal(1, payload.GetProperty("queueMetrics").GetProperty("pendingPayments").GetInt32());
        Assert.True(payload.GetProperty("queueMetrics").GetProperty("oldestDeviceMessageAgeSeconds").GetInt64() > 0);
        Assert.True(payload.GetProperty("queueMetrics").GetProperty("oldestPendingTelemetryDispatchAgeSeconds").GetInt64() > 0);
        Assert.True(payload.GetProperty("queueMetrics").GetProperty("oldestProcessingTelemetryDispatchAgeSeconds").GetInt64() > 0);
        Assert.True(payload.GetProperty("queueMetrics").GetProperty("oldestWebhookAgeSeconds").GetInt64() > 0);
        Assert.True(payload.GetProperty("queueMetrics").GetProperty("oldestPendingPaymentAgeSeconds").GetInt64() > 0);
        Assert.DoesNotContain("secret-payload", payload.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-command-payload", payload.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-status-payload", payload.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private provider response", payload.ToString(), StringComparison.Ordinal);
    }
}
