using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class DeploymentReadinessServiceTests
{
    [Fact]
    public async Task DeploymentPreflightFailsClosedWhenSessionAdmissionGateIsMissing()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var readiness = new DeploymentReadinessService(db);

        Assert.Equal("session_admission_gate_unavailable", await readiness.GetBlockerAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DeploymentPreflightAllowsOpenSalesWhenConnectionsAreRoutableAndBlocksUnresolvedOperations()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.SessionAdmissionGates.Add(new SessionAdmissionGate
        {
            Id = SessionAdmissionGate.SingletonId,
            NewSessionsPaused = false,
            UpdatedBy = "test",
            Reason = "continuity release test"
        });
        db.SystemSettings.Add(new SystemSettings { ApplicationFeePercent = 5m });

        var company = new Company { Name = "Empresa de teste", Cnpj = "11222333000181" };
        var machine = new Machine
        {
            Id = "preflight-machine", Name = "Máquina de teste", SerialNumber = "PREFLIGHT-1",
            NormalizedSerialNumber = "PREFLIGHT-1", CompanyId = company.Id
        };
        db.Companies.Add(company);
        db.Machines.Add(machine);
        await db.SaveChangesAsync();

        var readiness = new DeploymentReadinessService(db);
        Assert.Null(await readiness.GetBlockerAsync(CancellationToken.None));

        var session = new MachineSession
        {
            MachineId = machine.Id,
            CompanyId = company.Id,
            State = MachineSessionStates.AwaitingSelection
        };
        db.MachineSessions.Add(session);
        await db.SaveChangesAsync();
        Assert.Equal("active_session_without_routable_machine", await readiness.GetBlockerAsync(CancellationToken.None));

        var connectionState = new MachineConnectionState
        {
            MachineId = machine.Id,
            CompanyId = company.Id,
            IsOnline = true,
            ConnectionId = "routed-session-connection",
            OwnerNodeId = "test-node",
            OwnerConnectionId = "routed-session-connection",
            CommandRoutingVersion = TelemetryConnectionManager.CurrentCommandRoutingVersion,
            OwnerLeaseExpiresAt = DateTime.UtcNow.AddMinutes(1),
            LastSeenAt = DateTime.UtcNow
        };
        db.MachineConnectionStates.Add(connectionState);
        await db.SaveChangesAsync();
        Assert.Null(await readiness.GetBlockerAsync(CancellationToken.None));

        session.ClosedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        Assert.Null(await readiness.GetBlockerAsync(CancellationToken.None));

        session.State = MachineSessionStates.ClosureIncomplete;
        await db.SaveChangesAsync();
        Assert.Equal("unresolved_session_closures", await readiness.GetBlockerAsync(CancellationToken.None));
        session.State = MachineSessionStates.ClosureVerified;
        await db.SaveChangesAsync();
        Assert.Null(await readiness.GetBlockerAsync(CancellationToken.None));

        var dispatch = new TelemetryCommandDispatch
        {
            CompanyId = company.Id,
            MachineId = machine.Id,
            OwnerNodeId = "test-node",
            Status = "Pending"
        };
        db.TelemetryCommandDispatches.Add(dispatch);
        await db.SaveChangesAsync();
        Assert.Equal("telemetry_commands_in_flight", await readiness.GetBlockerAsync(CancellationToken.None));

        dispatch.Status = "Processing";
        await db.SaveChangesAsync();
        Assert.Equal("telemetry_commands_in_flight", await readiness.GetBlockerAsync(CancellationToken.None));

        dispatch.Status = "Indeterminate";
        await db.SaveChangesAsync();
        Assert.Equal("telemetry_commands_in_flight", await readiness.GetBlockerAsync(CancellationToken.None));

        dispatch.Operation = "Command";
        db.TelemetryCommandResolutions.Add(new TelemetryCommandResolution
        {
            TelemetryCommandDispatchId = dispatch.Id,
            OperatorName = "test-operator",
            Reason = "Resultado conferido em ensaio.",
            EvidenceReference = "ticket-test-dispatch"
        });
        await db.SaveChangesAsync();
        Assert.Null(await readiness.GetBlockerAsync(CancellationToken.None));
        db.TelemetryCommandResolutions.RemoveRange(db.TelemetryCommandResolutions);
        await db.SaveChangesAsync();

        dispatch.Operation = "MdbStatus";
        await db.SaveChangesAsync();
        Assert.Null(await readiness.GetBlockerAsync(CancellationToken.None));

        dispatch.Status = "Failed";
        dispatch.Operation = "Command";
        dispatch.Error = "ack_timeout";
        await db.SaveChangesAsync();
        Assert.Equal("telemetry_commands_in_flight", await readiness.GetBlockerAsync(CancellationToken.None));
        db.TelemetryCommandResolutions.Add(new TelemetryCommandResolution
        {
            TelemetryCommandDispatchId = dispatch.Id,
            OperatorName = "test-operator",
            Reason = "ACK expirado e estado físico conferido.",
            EvidenceReference = "ticket-test-failed-dispatch"
        });
        await db.SaveChangesAsync();
        Assert.Null(await readiness.GetBlockerAsync(CancellationToken.None));
        db.TelemetryCommandResolutions.RemoveRange(db.TelemetryCommandResolutions);
        await db.SaveChangesAsync();

        // This dispatch was resolved above; complete it before testing the legacy command path.
        dispatch.Status = "Completed";
        await db.SaveChangesAsync();

        db.TelemetryCommands.Add(new TelemetryCommand
        {
            CompanyId = company.Id,
            MachineId = machine.Id,
            Command = "ABRIR_SESSAO",
            Status = "failed",
            Error = "ack_timeout"
        });
        await db.SaveChangesAsync();
        Assert.Equal("telemetry_commands_in_flight", await readiness.GetBlockerAsync(CancellationToken.None));

        var uncertainCommand = await db.TelemetryCommands.SingleAsync();
        db.TelemetryCommandResolutions.Add(new TelemetryCommandResolution
        {
            TelemetryCommandId = uncertainCommand.Id,
            OperatorName = "test-operator",
            Reason = "Estado físico conferido em ensaio.",
            EvidenceReference = "ticket-test-1"
        });
        await db.SaveChangesAsync();
        Assert.Null(await readiness.GetBlockerAsync(CancellationToken.None));

        db.TelemetryCommandResolutions.RemoveRange(db.TelemetryCommandResolutions);
        db.TelemetryCommands.RemoveRange(db.TelemetryCommands);
        await db.SaveChangesAsync();

        dispatch.Status = "Completed";
        await db.SaveChangesAsync();
        Assert.Null(await readiness.GetBlockerAsync(CancellationToken.None));

        var inboxMessage = new DeviceMessageInbox
        {
            IdempotencyKey = "preflight-inbox-message",
            MachineId = machine.Id,
            CompanyId = company.Id,
            Serial = machine.SerialNumber,
            ConnectionId = "preflight-connection",
            MessageId = 42,
            DataJson = "{\"type\":\"vend.success\"}",
            Status = "Pending"
        };
        db.DeviceMessageInbox.Add(inboxMessage);
        await db.SaveChangesAsync();
        Assert.Equal("device_messages_unresolved", await readiness.GetBlockerAsync(CancellationToken.None));

        inboxMessage.Status = "Processed";
        await db.SaveChangesAsync();
        Assert.Null(await readiness.GetBlockerAsync(CancellationToken.None));

        inboxMessage.Status = "Failed";
        await db.SaveChangesAsync();
        Assert.Equal("device_messages_unresolved", await readiness.GetBlockerAsync(CancellationToken.None));

        inboxMessage.Status = "Processed";
        db.WebhookInboxMessages.Add(new WebhookInboxMessage
        {
            EventKey = "preflight-webhook-event",
            PaymentId = "preflight-payment",
            EventType = "payment",
            EventAction = "payment.updated",
            Format = "test",
            AuthenticationMode = "test",
            Status = "Pending"
        });
        await db.SaveChangesAsync();
        Assert.Equal("payment_webhooks_unresolved", await readiness.GetBlockerAsync(CancellationToken.None));

        var webhookMessage = await db.WebhookInboxMessages.SingleAsync();
        webhookMessage.Status = "Failed";
        await db.SaveChangesAsync();
        Assert.Equal("payment_webhooks_unresolved", await readiness.GetBlockerAsync(CancellationToken.None));

        webhookMessage.Status = "Processed";
        await db.SaveChangesAsync();
        Assert.Null(await readiness.GetBlockerAsync(CancellationToken.None));

        var outboxMessage = new OutboxMessage
        {
            CompanyId = company.Id,
            MachineId = machine.Id,
            MessageType = "refund_payment",
            Status = "processing"
        };
        db.OutboxMessages.Add(outboxMessage);
        await db.SaveChangesAsync();
        Assert.Equal("financial_outbox_in_flight", await readiness.GetBlockerAsync(CancellationToken.None));

        outboxMessage.Status = "pending";
        await db.SaveChangesAsync();
        Assert.Null(await readiness.GetBlockerAsync(CancellationToken.None));

        outboxMessage.Status = "failed";
        await db.SaveChangesAsync();
        Assert.Null(await readiness.GetBlockerAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DeploymentPreflightBlocksOnlineMachineWithoutRoutableOwnerUntilConnectionIsClaimed()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        db.SessionAdmissionGates.Add(new SessionAdmissionGate
        {
            Id = SessionAdmissionGate.SingletonId,
            NewSessionsPaused = true,
            UpdatedBy = "test",
            Reason = "release test"
        });
        db.SystemSettings.Add(new SystemSettings { ApplicationFeePercent = 5m });
        var company = new Company { Name = "Empresa de release", ActivationStatus = CompanyActivationStatuses.Active };
        var machine = new Machine
        {
            Id = "release-legacy-machine",
            Name = "Máquina com conexão legada",
            SerialNumber = "RELEASE-LEGACY-1",
            NormalizedSerialNumber = "RELEASE-LEGACY-1",
            CompanyId = company.Id
        };
        db.Companies.Add(company);
        db.Machines.Add(machine);
        await db.SaveChangesAsync();

        const string connectionId = "legacy-socket-1";
        db.MachineConnectionStates.Add(new MachineConnectionState
        {
            MachineId = machine.Id,
            CompanyId = company.Id,
            IsOnline = true,
            ConnectionId = connectionId,
            LastSeenAt = DateTime.UtcNow,
            OwnerNodeId = null,
            OwnerConnectionId = null,
            CommandRoutingVersion = null
        });
        await db.SaveChangesAsync();

        var readiness = new DeploymentReadinessService(db);
        Assert.Equal("legacy_owner_api_unavailable", await readiness.GetBlockerAsync(CancellationToken.None));

        var state = await db.MachineConnectionStates.SingleAsync(item => item.MachineId == machine.Id);
        state.OwnerNodeId = "candidate-node";
        state.OwnerConnectionId = connectionId;
        state.CommandRoutingVersion = TelemetryConnectionManager.CurrentCommandRoutingVersion;
        await db.SaveChangesAsync();

        Assert.Equal("machine_connections_unroutable", await readiness.GetBlockerAsync(CancellationToken.None));

        state.OwnerLeaseExpiresAt = DateTime.UtcNow.Add(TelemetryConnectionLeaseWorker.LeaseDuration);
        await db.SaveChangesAsync();
        Assert.Null(await readiness.GetBlockerAsync(CancellationToken.None));

        state.OwnerLeaseExpiresAt = DateTime.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync();
        Assert.Equal("machine_connections_unroutable", await readiness.GetBlockerAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DeploymentPreflightAllowsLegacyOwnerOnlyWhenPrivateLegacyApiIsReady()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var company = new Company { Name = "Empresa de coexistência", Cnpj = "11222333000181" };
        var machine = new Machine
        {
            Id = "legacy-relay-machine",
            Name = "Máquina com socket legado",
            SerialNumber = "LEGACY-RELAY-1",
            NormalizedSerialNumber = "LEGACY-RELAY-1",
            CompanyId = company.Id
        };
        db.Companies.Add(company);
        db.Machines.Add(machine);
        db.SessionAdmissionGates.Add(new SessionAdmissionGate
        {
            Id = SessionAdmissionGate.SingletonId,
            NewSessionsPaused = false,
            UpdatedBy = "test",
            Reason = "side-by-side legacy owner test"
        });
        db.SystemSettings.Add(new SystemSettings { ApplicationFeePercent = 5m });
        db.MachineConnectionStates.Add(new MachineConnectionState
        {
            MachineId = machine.Id,
            CompanyId = company.Id,
            IsOnline = true,
            ConnectionId = "legacy-relay-socket",
            LastSeenAt = DateTime.UtcNow
        });
        db.MachineSessions.Add(new MachineSession
        {
            MachineId = machine.Id,
            CompanyId = company.Id,
            State = MachineSessionStates.AwaitingSelection
        });
        await db.SaveChangesAsync();

        var readyHandler = new LegacyApiHealthHandler(HttpStatusCode.OK, "{\"status\":\"Healthy\"}");
        var readyRelay = CreateLegacyRelay(readyHandler);
        var readiness = new DeploymentReadinessService(db, readyRelay);
        Assert.Null(await readiness.GetBlockerAsync(CancellationToken.None));
        Assert.Equal("http://legacy-api.internal/health", readyHandler.RequestUri?.ToString());

        var unavailableRelay = CreateLegacyRelay(
            new LegacyApiHealthHandler(HttpStatusCode.ServiceUnavailable, "{\"status\":\"NotReady\"}"));
        var unavailableReadiness = new DeploymentReadinessService(db, unavailableRelay);
        Assert.Equal("legacy_owner_api_unavailable", await unavailableReadiness.GetBlockerAsync(CancellationToken.None));

        var nonLegacyConnection = await db.MachineConnectionStates.SingleAsync();
        nonLegacyConnection.OwnerNodeId = "unknown-owner";
        nonLegacyConnection.OwnerConnectionId = nonLegacyConnection.ConnectionId;
        await db.SaveChangesAsync();
        Assert.Equal("machine_connections_unroutable", await readiness.GetBlockerAsync(CancellationToken.None));
    }

    private static LegacyTelemetryRequestRelay CreateLegacyRelay(LegacyApiHealthHandler handler)
    {
        var client = new HttpClient(handler);
        var factory = new FixedHttpClientFactory(client);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telemetry:LegacyApiBaseUrl"] = "http://legacy-api.internal"
            })
            .Build();
        return new LegacyTelemetryRequestRelay(factory, configuration);
    }

    private sealed class FixedHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class LegacyApiHealthHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
