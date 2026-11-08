using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class SessionOrchestratorFlowTests
{
    [Theory]
    [InlineData("idle_state", true, true)]
    [InlineData("enabled_state", true, false)]
    [InlineData("idle_state", false, false)]
    public async Task StartRecoversOnlyFromFreshCorrelatedIdleStatus(string status, bool success, bool recovered)
    {
        var connections = new StartConnections { Status = status, StatusSuccess = success };
        await using var scenario = await TestScenario.CreateWithoutTransactionAsync(
            MachineSessionStates.Completed, addMockIntegration: true, connections: connections);
        var previous = await scenario.Db.MachineSessions.SingleAsync();
        previous.ClosedAt = DateTime.UtcNow;
        await scenario.Db.SaveChangesAsync();

        var session = await scenario.Orchestrator.StartSessionAsync(
            scenario.MachineId, scenario.CompanyId, null, "panel", CancellationToken.None);

        Assert.Equal(recovered ? MachineSessionStates.AwaitingSelection : MachineSessionStates.Opening, session.State);
        Assert.Equal(recovered, session.SelectionDeadlineAt.HasValue);
        Assert.Equal(recovered ? 1 : 0, await scenario.Db.TelemetryEvents.CountAsync(x => x.EventType == "session.open_already"));
        var command = await scenario.Db.TelemetryCommands.SingleAsync(x => x.Command == "ABRIR_SESSAO");
        Assert.Equal("acknowledged", command.Status);
        Assert.Equal(1, connections.OpenCommands);
        if (recovered)
        {
            await scenario.SendRawAsync(new { command = "vend", type = "request", itemPrice = 200, itemNumber = 2 });
            var after = await scenario.Db.MachineSessions.AsNoTracking().SingleAsync(x => x.Id == session.Id);
            Assert.Equal(MachineSessionStates.PaymentPending, after.State);
            Assert.Single(await scenario.Db.PaymentTransactions.ToListAsync());
            await scenario.SendRawAsync(new { command = "vend", type = "request", itemPrice = 200, itemNumber = 2 });
            Assert.Single(await scenario.Db.PaymentTransactions.ToListAsync());
            await scenario.SendAsync("pool", "begin_session", false);
            after = await scenario.Db.MachineSessions.AsNoTracking().SingleAsync(x => x.Id == session.Id);
            Assert.Equal(MachineSessionStates.PaymentPending, after.State);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StartDoesNotRecoverFromStaleOrUncorrelatedStatus(bool stale)
    {
        var connections = new StartConnections { Status = "idle_state", Stale = stale, Uncorrelated = !stale };
        await using var scenario = await TestScenario.CreateWithoutTransactionAsync(MachineSessionStates.Completed, connections: connections);
        (await scenario.Db.MachineSessions.SingleAsync()).ClosedAt = DateTime.UtcNow;
        await scenario.Db.SaveChangesAsync();
        var session = await scenario.Orchestrator.StartSessionAsync(scenario.MachineId, scenario.CompanyId, null, "panel", CancellationToken.None);
        Assert.Equal(MachineSessionStates.Opening, session.State);
        Assert.Empty(await scenario.Db.TelemetryEvents.Where(x => x.EventType == "session.open_already").ToListAsync());
    }

    [Fact]
    public async Task StartDoesNotRecoverOrSendCommandWhenSessionAlreadyExists()
    {
        var connections = new StartConnections();
        await using var scenario = await TestScenario.CreateWithoutTransactionAsync(MachineSessionStates.AwaitingSelection, connections: connections);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.Orchestrator.StartSessionAsync(
            scenario.MachineId, scenario.CompanyId, null, "panel", CancellationToken.None));
        Assert.Equal("session_already_active", error.Message);
        Assert.Equal(0, connections.OpenCommands);
    }

    [Fact]
    public async Task BeginSessionBeforeStatusDoesNotDuplicateRecoveryOrResetDeadline()
    {
        var connections = new StartConnections { Status = "idle_state" };
        await using var scenario = await TestScenario.CreateWithoutTransactionAsync(MachineSessionStates.Completed, connections: connections);
        (await scenario.Db.MachineSessions.SingleAsync()).ClosedAt = DateTime.UtcNow;
        await scenario.Db.SaveChangesAsync();
        connections.BeforeStatus = () => scenario.SendAsync("pool", "begin_session", false);
        var session = await scenario.Orchestrator.StartSessionAsync(scenario.MachineId, scenario.CompanyId, null, "panel", CancellationToken.None);
        Assert.Equal(MachineSessionStates.AwaitingSelection, session.State);
        Assert.NotNull(session.SelectionDeadlineAt);
        Assert.Empty(await scenario.Db.TelemetryEvents.Where(x => x.EventType == "session.open_already").ToListAsync());
    }

    [Fact]
    public async Task SelectionDuringOpeningCanBeRetriedAfterBeginSession()
    {
        var connections = new StartConnections();
        await using var scenario = await TestScenario.CreateWithoutTransactionAsync(
            MachineSessionStates.Opening, addMockIntegration: true, connections: connections);
        var selection = new { command = "vend", type = "request", itemPrice = 200, itemNumber = 2 };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.SendRawAsync(selection));
        Assert.Equal("session_opening_in_progress", error.Message);
        scenario.Db.ChangeTracker.Clear(); // The inbox retries in a fresh scope.
        await scenario.SendAsync("pool", "begin_session", false);
        await scenario.SendRawAsync(selection);
        Assert.Equal(MachineSessionStates.PaymentPending, (await scenario.ReloadSessionAsync()).State);
        Assert.Single(await scenario.Db.PaymentTransactions.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MenuClosureEndsUnselectedSessionAndAllowsReopening(bool automatic)
    {
        var connections = new StartConnections { Status = "enabled_state" };
        await using var scenario = await TestScenario.CreateWithoutTransactionAsync(MachineSessionStates.AwaitingSelection, connections: connections);
        var machine = await scenario.Db.Machines.SingleAsync();
        machine.AutoOpenSessionEnabled = automatic;
        scenario.Db.MachineConnectionStates.Add(new MachineConnectionState {
            MachineId = scenario.MachineId, CompanyId = scenario.CompanyId, ConnectionId = "connection-test",
            MonitoringEnabled = true, IsOnline = true, AutoOpenLastAttemptAt = DateTime.UtcNow
        });
        await scenario.Db.SaveChangesAsync();
        await scenario.SendRawAsync(new { command = "status", status = "idle_state" });
        await scenario.SendRawAsync(new { command = "status", status = "idle_state" });
        await scenario.SendRawAsync(new { command = "status", status = "inactive_state" });
        await scenario.SendRawAsync(new { command = "status", status = "inactive_state" });
        Assert.Null((await scenario.ReloadSessionAsync()).ClosedAt);
        await scenario.SendRawAsync(new { command = "status", status = "enabled_state" });
        var previous = await scenario.ReloadSessionAsync();
        Assert.Equal(MachineSessionStates.ClosedByDeviceBeforeSelection, previous.State);
        Assert.Equal("menu_closed_without_selection", previous.CloseReason);
        Assert.Equal(1, await scenario.EventCountAsync("session.menu_closed"));
        if (!automatic)
            await scenario.Orchestrator.StartSessionAsync(scenario.MachineId, scenario.CompanyId, null, "panel", CancellationToken.None);
        Assert.Equal(1, connections.OpenCommands);
        await scenario.SendRawAsync(new { command = "status", status = "enabled_state" });
        Assert.Equal(1, connections.OpenCommands);
        Assert.Single(await scenario.Db.MachineSessions.Where(x => x.ClosedAt == null).ToListAsync());
    }

    [Theory]
    [InlineData("inactive_state", "enabled_state")]
    [InlineData("idle_state", "enabled_state")]
    [InlineData("idle_state", "vend_state")]
    public async Task IncompleteMenuSequenceDoesNotCloseSession(string first, string second)
    {
        await using var scenario = await TestScenario.CreateWithoutTransactionAsync(MachineSessionStates.AwaitingSelection);
        await scenario.SendRawAsync(new { command = "status", status = first });
        await scenario.SendRawAsync(new { command = "status", status = second });
        Assert.Null((await scenario.ReloadSessionAsync()).ClosedAt);
        Assert.Equal(0, await scenario.EventCountAsync("session.menu_closed"));
    }

    [Theory]
    [InlineData(MachineSessionStates.PaymentPending)]
    [InlineData(MachineSessionStates.AwaitingDeliveryResult)]
    public async Task MenuSequenceDoesNotCloseSessionWithPayment(string state)
    {
        await using var scenario = await TestScenario.CreatePaidAsync(state);
        await scenario.SendRawAsync(new { command = "status", status = "idle_state" });
        await scenario.SendRawAsync(new { command = "status", status = "inactive_state" });
        await scenario.SendRawAsync(new { command = "status", status = "enabled_state" });
        var session = await scenario.ReloadSessionAsync();
        Assert.Null(session.ClosedAt);
        Assert.Equal(state, session.State);
    }

    [Fact]
    public async Task MenuSequenceDoesNotCloseSessionWithSelectedItem()
    {
        await using var scenario = await TestScenario.CreateWithoutTransactionAsync(MachineSessionStates.AwaitingSelection);
        (await scenario.Db.MachineSessions.SingleAsync()).ItemNumber = 3;
        await scenario.Db.SaveChangesAsync();
        await scenario.SendRawAsync(new { command = "status", status = "idle_state" });
        await scenario.SendRawAsync(new { command = "status", status = "inactive_state" });
        await scenario.SendRawAsync(new { command = "status", status = "enabled_state" });
        Assert.Null((await scenario.ReloadSessionAsync()).ClosedAt);
    }

    [Fact]
    public async Task MenuSequenceDoesNotSurviveAConnectionChange()
    {
        await using var scenario = await TestScenario.CreateWithoutTransactionAsync(MachineSessionStates.AwaitingSelection);
        await scenario.SendRawAsync(new { command = "status", status = "idle_state" });
        await scenario.SendRawAsync(new { command = "status", status = "inactive_state" });
        var state = await scenario.Db.MachineConnectionStates.SingleAsync();
        state.ConnectionId = "new-connection";
        await scenario.Db.SaveChangesAsync();
        await scenario.Orchestrator.HandleDeviceMessageAsync(new InboundDeviceMessage(
            scenario.MachineId, scenario.CompanyId, "serial", "new-connection", 100,
            "{\"command\":\"status\",\"status\":\"enabled_state\"}", DateTime.UtcNow.AddSeconds(1)), CancellationToken.None);
        Assert.Null((await scenario.ReloadSessionAsync()).ClosedAt);
    }

    [Fact]
    public async Task ManualStartIsBlockedDuringRecognizedMenuEvenWithoutMdbEnforcement()
    {
        var connections = new StartConnections();
        await using var scenario = await TestScenario.CreateWithoutTransactionAsync(MachineSessionStates.Completed, connections: connections);
        (await scenario.Db.MachineSessions.SingleAsync()).ClosedAt = DateTime.UtcNow;
        scenario.Db.MachineConnectionStates.Add(new MachineConnectionState { MachineId = scenario.MachineId,
            CompanyId = scenario.CompanyId, ConnectionId = "connection-test", MonitoringEnabled = true });
        await scenario.Db.SaveChangesAsync();
        await scenario.SendRawAsync(new { command = "status", status = "idle_state" });
        await scenario.SendRawAsync(new { command = "status", status = "inactive_state" });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.Orchestrator.StartSessionAsync(
            scenario.MachineId, scenario.CompanyId, null, "panel", CancellationToken.None));
        Assert.Equal("mdb_menu_open", error.Message);
        Assert.Equal(0, connections.OpenCommands);
    }

    [Fact]
    public async Task ExpiredMenuSequenceCannotCloseSession()
    {
        await using var scenario = await TestScenario.CreateWithoutTransactionAsync(MachineSessionStates.AwaitingSelection);
        await scenario.SendRawAsync(new { command = "status", status = "idle_state" });
        await scenario.SendRawAsync(new { command = "status", status = "inactive_state" });
        var connection = await scenario.Db.MachineConnectionStates.SingleAsync();
        connection.MdbMenuUpdatedAt = DateTime.UtcNow.AddMinutes(-5);
        await scenario.Db.SaveChangesAsync();
        await scenario.SendRawAsync(new { command = "status", status = "enabled_state" });
        Assert.Null((await scenario.ReloadSessionAsync()).ClosedAt);
    }

    [Fact]
    public async Task OlderStatusCannotAdvanceOrRewindMenuSequence()
    {
        await using var scenario = await TestScenario.CreateWithoutTransactionAsync(MachineSessionStates.AwaitingSelection);
        await scenario.SendRawAsync(new { command = "status", status = "idle_state" });
        await scenario.SendRawAsync(new { command = "status", status = "inactive_state" });
        await scenario.Orchestrator.HandleDeviceMessageAsync(new InboundDeviceMessage(
            scenario.MachineId, scenario.CompanyId, "serial", "connection-test", 99,
            "{\"command\":\"status\",\"status\":\"enabled_state\"}", DateTime.UtcNow.AddMinutes(-1)), CancellationToken.None);
        Assert.Null((await scenario.ReloadSessionAsync()).ClosedAt);
        Assert.Equal("inactive", (await scenario.Db.MachineConnectionStates.SingleAsync()).MdbMenuPhase);
    }

    [Fact]
    public async Task NormalDeviceEndBeforeEnabledStillReopensAutomatically()
    {
        var connections = new StartConnections { Status = "enabled_state" };
        await using var scenario = await TestScenario.CreateWithoutTransactionAsync(MachineSessionStates.AwaitingSelection, connections: connections);
        (await scenario.Db.Machines.SingleAsync()).AutoOpenSessionEnabled = true;
        scenario.Db.MachineConnectionStates.Add(new MachineConnectionState { MachineId = scenario.MachineId,
            CompanyId = scenario.CompanyId, ConnectionId = "connection-test", MonitoringEnabled = true,
            AutoOpenLastAttemptAt = DateTime.UtcNow });
        await scenario.Db.SaveChangesAsync();
        await scenario.SendRawAsync(new { command = "status", status = "idle_state" });
        await scenario.SendRawAsync(new { command = "status", status = "inactive_state" });
        await scenario.SendAsync("pool", "end_session", false);
        await scenario.SendRawAsync(new { command = "status", status = "enabled_state" });
        Assert.Equal(1, connections.OpenCommands);
        Assert.Equal(0, await scenario.EventCountAsync("session.menu_closed"));
    }

    [Fact]
    public async Task CompletedMenuCanResumeAutomaticOpenAfterInterruptedAttempt()
    {
        var connections = new StartConnections { Status = "enabled_state" };
        await using var scenario = await TestScenario.CreateWithoutTransactionAsync(MachineSessionStates.AwaitingSelection, connections: connections);
        (await scenario.Db.Machines.SingleAsync()).AutoOpenSessionEnabled = true;
        scenario.Db.MachineConnectionStates.Add(new MachineConnectionState { MachineId = scenario.MachineId,
            CompanyId = scenario.CompanyId, ConnectionId = "connection-test", MonitoringEnabled = false,
            AutoOpenLastAttemptAt = DateTime.UtcNow });
        await scenario.Db.SaveChangesAsync();
        await scenario.SendRawAsync(new { command = "status", status = "idle_state" });
        await scenario.SendRawAsync(new { command = "status", status = "inactive_state" });
        await scenario.SendRawAsync(new { command = "status", status = "enabled_state" });
        Assert.Equal(0, connections.OpenCommands);
        scenario.Db.ChangeTracker.Clear();
        var persisted = await scenario.Db.MachineConnectionStates.SingleAsync();
        Assert.Equal("ready", persisted.MdbMenuPhase);
        persisted.MonitoringEnabled = true;
        await scenario.Db.SaveChangesAsync();
        await scenario.SendRawAsync(new { command = "status", status = "enabled_state" });
        Assert.Equal(1, connections.OpenCommands);
    }

    [Theory]
    [InlineData("idle_state", "ClosingWithoutSelection", true)]
    [InlineData("enabled_state", "ClosedByDeviceBeforeSelection", false)]
    [InlineData("vend_state", "AwaitingSelection", false)]
    [InlineData("unavailable", "AwaitingSelection", false)]
    public async Task CancellationClosesOnlyAfterConfirmingMdbState(string status, string expectedState, bool sendsClose)
    {
        var connections = new StartConnections { Status = status, StatusSuccess = status != "unavailable" };
        await using var scenario = await TestScenario.CreateWithoutTransactionAsync(MachineSessionStates.AwaitingSelection, connections: connections);
        if (status == "unavailable")
            connections.Uncorrelated = true;
        if (status is "idle_state" or "enabled_state")
        {
            var closed = await scenario.Orchestrator.CancelSessionAsync(scenario.SessionId, scenario.CompanyId,
                Guid.NewGuid(), "user_cancelled", CancellationToken.None);
            Assert.Equal(expectedState, closed.State);
            Assert.Equal(sendsClose ? 1 : 0, await scenario.Db.TelemetryCommands.CountAsync(command => command.Command == "FECHAR_SESSAO"));
            if (!sendsClose)
                Assert.Equal(1, await scenario.EventCountAsync("session.mdb_reset_suspected"));
        }
        else
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.Orchestrator.CancelSessionAsync(
                scenario.SessionId, scenario.CompanyId, Guid.NewGuid(), "user_cancelled", CancellationToken.None));
            Assert.Equal("mdb_status_unavailable", error.Message);
            Assert.Equal(0, await scenario.Db.TelemetryCommands.CountAsync(command => command.Command == "FECHAR_SESSAO"));
        }
    }

    [Fact]
    public async Task EnabledStatusRecoversClosureIncompleteSessionWithoutSale()
    {
        await using var scenario = await TestScenario.CreateWithoutTransactionAsync(MachineSessionStates.ClosureIncomplete);
        var session = await scenario.Db.MachineSessions.SingleAsync();
        session.ClosedAt = DateTime.UtcNow.AddSeconds(-40);
        session.CloseReason = "ack_timeout";
        await scenario.Db.SaveChangesAsync();
        await scenario.SendRawAsync(new { command = "status", status = "enabled_state" });
        var recovered = await scenario.ReloadSessionAsync();
        Assert.Equal(MachineSessionStates.ClosedByDeviceBeforeSelection, recovered.State);
        Assert.Equal("possible_mdb_reset", recovered.CloseReason);
        Assert.Equal(1, await scenario.EventCountAsync("session.mdb_reset_suspected"));
    }

    [Fact]
    public async Task SessionStartAfterShutdownBeginsIsRejectedBeforeDatabaseChanges()
    {
        using var lifetime = new ApiReadinessEndpointTests.TestApplicationLifetime();
        await using var scenario = await TestScenario.CreateWithoutTransactionAsync(
            MachineSessionStates.AwaitingSelection,
            lifetime: lifetime);
        var sessionCount = await scenario.Db.MachineSessions.CountAsync();

        lifetime.BeginStopping();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.Orchestrator.StartSessionAsync(
            scenario.MachineId,
            scenario.CompanyId,
            null,
            "manual",
            CancellationToken.None));

        Assert.Equal("server_restarting", error.Message);
        Assert.Equal(sessionCount, await scenario.Db.MachineSessions.CountAsync());
    }

    [Fact]
    public async Task SessionStartForOnlineMachineWithoutCommandRouteIsRejectedBeforeCreatingSession()
    {
        await using var scenario = await TestScenario.CreateWithoutTransactionAsync(MachineSessionStates.Completed);
        scenario.Db.MachineConnectionStates.Add(new MachineConnectionState
        {
            MachineId = scenario.MachineId,
            CompanyId = scenario.CompanyId,
            IsOnline = true,
            MonitoringEnabled = true,
            LastSeenAt = DateTime.UtcNow,
            OwnerNodeId = null
        });
        await scenario.Db.SaveChangesAsync();
        var sessionCount = await scenario.Db.MachineSessions.CountAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.Orchestrator.StartSessionAsync(
            scenario.MachineId,
            scenario.CompanyId,
            null,
            "manual",
            CancellationToken.None));

        Assert.Equal("machine_owner_unknown", error.Message);
        Assert.Equal(sessionCount, await scenario.Db.MachineSessions.CountAsync());
        Assert.Empty(await scenario.Db.TelemetryCommands.ToListAsync());
    }

    [Fact]
    public async Task SessionStartDuringControlledReleaseIsRejectedBeforeCreatingSession()
    {
        await using var scenario = await TestScenario.CreateWithoutTransactionAsync(MachineSessionStates.Completed);
        var priorSession = await scenario.Db.MachineSessions.SingleAsync(x => x.Id == scenario.SessionId);
        priorSession.ClosedAt = DateTime.UtcNow;
        scenario.Db.MachineConnectionStates.Add(new MachineConnectionState
        {
            MachineId = scenario.MachineId,
            CompanyId = scenario.CompanyId,
            IsOnline = true,
            MonitoringEnabled = true,
            LastSeenAt = DateTime.UtcNow,
            ConnectionId = "another-compatible-connection",
            OwnerNodeId = "another-compatible-node",
            OwnerConnectionId = "another-compatible-connection",
            CommandRoutingVersion = TelemetryConnectionManager.CurrentCommandRoutingVersion
        });
        var gate = await scenario.Db.SessionAdmissionGates.SingleAsync();
        gate.NewSessionsPaused = true;
        await scenario.Db.SaveChangesAsync();
        var sessionCount = await scenario.Db.MachineSessions.CountAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.Orchestrator.StartSessionAsync(
            scenario.MachineId,
            scenario.CompanyId,
            null,
            "manual",
            CancellationToken.None));

        Assert.Equal("deployment_draining", error.Message);
        Assert.Equal(sessionCount, await scenario.Db.MachineSessions.CountAsync());
        Assert.Empty(await scenario.Db.TelemetryCommandDispatches.ToListAsync());
    }

    [Fact]
    public async Task CancellingSessionOnOnlineMachineWithoutCommandRouteDoesNotChangeSessionOrPayment()
    {
        await using var scenario = await TestScenario.CreatePaidAsync(
            MachineSessionStates.PaymentPending,
            transactionStatus: "Pending");
        scenario.Db.MachineConnectionStates.Add(new MachineConnectionState
        {
            MachineId = scenario.MachineId,
            CompanyId = scenario.CompanyId,
            IsOnline = true,
            MonitoringEnabled = true,
            LastSeenAt = DateTime.UtcNow,
            OwnerNodeId = null
        });
        await scenario.Db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.Orchestrator.CancelSessionAsync(
            scenario.SessionId,
            scenario.CompanyId,
            Guid.NewGuid(),
            "user_cancelled",
            CancellationToken.None));

        Assert.Equal("machine_owner_unknown", error.Message);
        var session = await scenario.ReloadSessionAsync();
        Assert.Equal(MachineSessionStates.PaymentPending, session.State);
        Assert.Equal("Pending", session.Transaction!.Status);
        Assert.Equal(0, await scenario.OutboxCountAsync("cancel_payment"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulDelivery_CompletesAndClosesSession(bool includeTransactionId)
    {
        await using var scenario = await TestScenario.CreatePaidAsync(MachineSessionStates.PaymentApproved);

        await scenario.SendAsync("pool", "vend_approved", includeTransactionId);
        await scenario.SendAsync("vend", "success", includeTransactionId);

        var delivered = await scenario.ReloadSessionAsync();
        Assert.Equal(MachineSessionStates.Completed, delivered.State);
        Assert.Null(delivered.DeliveryDeadlineAt);
        Assert.Equal("Approved", delivered.Transaction!.Status);
        Assert.Equal(1, await scenario.EventCountAsync("delivery.success"));
        Assert.Equal(0, await scenario.EventCountAsync("refund.requested"));

        await scenario.CloseFromDeviceAsync(includeTransactionId);

        var closed = await scenario.ReloadSessionAsync();
        Assert.Equal(MachineSessionStates.Completed, closed.State);
        Assert.NotNull(closed.ClosedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeliveryFailure_RefundsAndClosesSession(bool includeTransactionId)
    {
        await using var scenario = await TestScenario.CreatePaidAsync(MachineSessionStates.AwaitingDeliveryResult);

        await scenario.SendAsync("vend", "failure", includeTransactionId, reason: "mechanism_error");

        var failed = await scenario.ReloadSessionAsync();
        Assert.Equal(MachineSessionStates.Refunded, failed.State);
        Assert.Equal("Refunded", failed.Transaction!.Status);
        Assert.Equal(1, await scenario.EventCountAsync("delivery.failure"));
        Assert.Equal(1, await scenario.EventCountAsync("refund.requested"));
        Assert.Equal(1, await scenario.EventCountAsync("refund.completed"));

        var deliveryFailure = await scenario.Db.DeliveryFailures.AsNoTracking().SingleAsync();
        Assert.Equal("mechanism_error", deliveryFailure.Reason);
        Assert.Equal("mechanism_error", deliveryFailure.OriginalReason);

        await scenario.CloseFromDeviceAsync(includeTransactionId);
        Assert.NotNull((await scenario.ReloadSessionAsync()).ClosedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeliveryFailureWithoutReason_UsesUnknownAndRefunds(bool includeTransactionId)
    {
        await using var scenario = await TestScenario.CreatePaidAsync(MachineSessionStates.AwaitingDeliveryResult);

        await scenario.SendAsync("vend", "failure", includeTransactionId);

        var session = await scenario.ReloadSessionAsync();
        var deliveryFailure = await scenario.Db.DeliveryFailures.AsNoTracking().SingleAsync();
        var refundRequested = await scenario.SingleEventAsync("refund.requested");

        Assert.Equal(MachineSessionStates.Refunded, session.State);
        Assert.Equal("unknown", deliveryFailure.Reason);
        Assert.Equal("unknown", deliveryFailure.OriginalReason);
        Assert.Equal("delivery_failure:unknown", refundRequested.Detail);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedPayment_EndsAsDenied(bool includeTransactionId)
    {
        await using var scenario = await TestScenario.CreatePaidAsync(
            MachineSessionStates.PaymentPending,
            transactionStatus: "Pending");

        await scenario.Orchestrator.HandlePaymentResultAsync(
            scenario.TransactionId,
            approved: false,
            "mercado_pago_webhook",
            CancellationToken.None);
        await scenario.SendAsync("pool", "vend_denied", includeTransactionId);
        await scenario.CloseFromDeviceAsync(includeTransactionId);

        var session = await scenario.ReloadSessionAsync();
        Assert.Equal(MachineSessionStates.Denied, session.State);
        Assert.Equal("Rejected", session.Transaction!.Status);
        Assert.NotNull(session.ClosedAt);
        Assert.Equal(1, await scenario.EventCountAsync("payment.denied"));
        Assert.Equal(0, await scenario.EventCountAsync("refund.requested"));
    }

    [Fact]
    public async Task DuplicatePaymentApproval_RecordsDuplicateWithoutSendingSecondCommandOrCreatingSale()
    {
        await using var scenario = await TestScenario.CreatePaidAsync(
            MachineSessionStates.PaymentPending,
            transactionStatus: "Pending");

        await scenario.Orchestrator.HandlePaymentResultAsync(
            scenario.TransactionId, approved: true, "mercado_pago_webhook", CancellationToken.None);
        await scenario.Orchestrator.HandlePaymentResultAsync(
            scenario.TransactionId, approved: true, "mercado_pago_webhook", CancellationToken.None);

        var session = await scenario.ReloadSessionAsync();
        Assert.Equal(MachineSessionStates.Refunded, session.State);
        Assert.Equal("Refunded", session.Transaction!.Status);
        Assert.Equal(1, await scenario.EventCountAsync("payment.approved"));
        Assert.Equal(1, await scenario.EventCountAsync("payment.duplicate_result"));
        Assert.Equal(1, await scenario.Db.TelemetryCommands.AsNoTracking()
            .CountAsync(command => command.SessionId == scenario.SessionId && command.Command == "VENDA_APROVADA"));
        Assert.Equal(1, await scenario.EventCountAsync("refund.requested"));
        Assert.Equal(1, await scenario.OutboxCountAsync("refund_payment"));
        Assert.Empty(await scenario.Db.SaleRecords.AsNoTracking()
            .Where(sale => sale.TransactionId == scenario.TransactionId).ToListAsync());
    }

    [Fact]
    public async Task ApprovedPaymentForLegacyOnlineOwnerKeepsRefundPendingWithoutDispatchingSaleCommand()
    {
        await using var scenario = await TestScenario.CreatePaidAsync(
            MachineSessionStates.PaymentPending,
            transactionStatus: "Pending");
        await scenario.SetProviderPaymentIdAsync($"provider-{Guid.NewGuid():N}");
        scenario.Db.MachineConnectionStates.Add(new MachineConnectionState
        {
            MachineId = scenario.MachineId,
            CompanyId = scenario.CompanyId,
            IsOnline = true,
            ConnectionId = "legacy-owner-connection",
            OwnerNodeId = "legacy-owner-node",
            OwnerConnectionId = "legacy-owner-connection",
            CommandRoutingVersion = null,
            LastSeenAt = DateTime.UtcNow
        });
        await scenario.Db.SaveChangesAsync();

        await scenario.Orchestrator.HandlePaymentResultAsync(
            scenario.TransactionId,
            approved: true,
            "mercado_pago_webhook",
            CancellationToken.None);

        var session = await scenario.ReloadSessionAsync();
        Assert.Equal(MachineSessionStates.RefundPending, session.State);
        Assert.Equal("RefundPending", session.Transaction!.Status);
        Assert.Equal(1, await scenario.EventCountAsync("payment.approved"));
        Assert.Equal(1, await scenario.EventCountAsync("refund.requested"));
        Assert.Equal(1, await scenario.OutboxCountAsync("refund_payment"));
        Assert.Equal(0, await scenario.EventCountAsync("refund.completed"));
        Assert.Equal(0, scenario.ProviderRequestCount);
        Assert.Empty(await scenario.Db.TelemetryCommandDispatches.AsNoTracking().ToListAsync());
        Assert.Empty(await scenario.Db.SaleRecords.AsNoTracking()
            .Where(sale => sale.TransactionId == scenario.TransactionId).ToListAsync());

        var refund = await scenario.SingleOutboxAsync("refund_payment");
        Assert.Equal("pending", refund.Status);
        Assert.Equal(1, refund.Attempts);
        Assert.Equal("provider_operation_failed", refund.LastError);
        var saleCommand = await scenario.Db.TelemetryCommands.AsNoTracking()
            .SingleAsync(command => command.SessionId == scenario.SessionId && command.Command == "VENDA_APROVADA");
        Assert.Equal("failed", saleCommand.Status);
    }

    [Fact]
    public async Task RejectedPaymentForLegacyOnlineOwnerRemainsOpenForOwnerDeadlineWorker()
    {
        await using var scenario = await TestScenario.CreatePaidAsync(
            MachineSessionStates.PaymentPending,
            transactionStatus: "Pending");
        scenario.Db.MachineConnectionStates.Add(new MachineConnectionState
        {
            MachineId = scenario.MachineId,
            CompanyId = scenario.CompanyId,
            IsOnline = true,
            ConnectionId = "legacy-owner-connection",
            OwnerNodeId = "legacy-owner-node",
            OwnerConnectionId = "legacy-owner-connection",
            CommandRoutingVersion = null,
            LastSeenAt = DateTime.UtcNow
        });
        await scenario.Db.SaveChangesAsync();

        await scenario.Orchestrator.HandlePaymentResultAsync(
            scenario.TransactionId,
            approved: false,
            "mercado_pago:rejected",
            CancellationToken.None);

        var deniedSession = await scenario.ReloadSessionAsync();
        var deniedTransaction = await scenario.Db.PaymentTransactions.AsNoTracking()
            .SingleAsync(item => item.Id == scenario.TransactionId);
        var deniedCommand = await scenario.Db.TelemetryCommands.AsNoTracking()
            .SingleAsync(command => command.SessionId == scenario.SessionId && command.Command == "VENDA_NEGADA");
        Assert.Equal(MachineSessionStates.DeniedAwaitingClosure, deniedSession.State);
        Assert.Equal("Rejected", deniedTransaction.Status);
        Assert.Equal("failed", deniedCommand.Status);
        Assert.Empty(await scenario.Db.TelemetryCommandDispatches.AsNoTracking().ToListAsync());
        Assert.Empty(await scenario.Db.SaleRecords.AsNoTracking()
            .Where(sale => sale.TransactionId == scenario.TransactionId).ToListAsync());

        var sessionWithExpiredClosureDeadline = await scenario.Db.MachineSessions
            .SingleAsync(item => item.Id == scenario.SessionId);
        sessionWithExpiredClosureDeadline.LastEventAt = DateTime.UtcNow.AddSeconds(-31);
        await scenario.Db.SaveChangesAsync();
        await scenario.Orchestrator.ProcessDeadlinesAsync(CancellationToken.None);

        var stillOwnedSession = await scenario.ReloadSessionAsync();
        Assert.Equal(MachineSessionStates.DeniedAwaitingClosure, stillOwnedSession.State);
        Assert.Null(stillOwnedSession.ClosedAt);
        Assert.Equal("mercado_pago:rejected", stillOwnedSession.CloseReason);
        Assert.Equal("Rejected", stillOwnedSession.Transaction!.Status);
        Assert.Empty(await scenario.Db.TelemetryCommandDispatches.AsNoTracking().ToListAsync());
        Assert.Empty(await scenario.Db.SaleRecords.AsNoTracking()
            .Where(sale => sale.TransactionId == scenario.TransactionId).ToListAsync());
    }

    [Fact]
    public async Task ExpiredPaymentDeadlineForLegacyOnlineOwnerIsLeftToPreviousApi()
    {
        await using var scenario = await TestScenario.CreatePaidAsync(
            MachineSessionStates.PaymentPending,
            transactionStatus: "Pending");
        scenario.Db.MachineConnectionStates.Add(new MachineConnectionState
        {
            MachineId = scenario.MachineId,
            CompanyId = scenario.CompanyId,
            IsOnline = true,
            ConnectionId = "legacy-owner-connection",
            OwnerNodeId = "legacy-owner-node",
            OwnerConnectionId = "legacy-owner-connection",
            CommandRoutingVersion = null,
            LastSeenAt = DateTime.UtcNow
        });
        var session = await scenario.Db.MachineSessions.SingleAsync(item => item.Id == scenario.SessionId);
        session.PaymentDeadlineAt = DateTime.UtcNow.AddSeconds(-1);
        await scenario.Db.SaveChangesAsync();

        await scenario.Orchestrator.ProcessDeadlinesAsync(CancellationToken.None);

        var unchanged = await scenario.ReloadSessionAsync();
        Assert.Equal(MachineSessionStates.PaymentPending, unchanged.State);
        Assert.Equal("Pending", unchanged.Transaction!.Status);
        Assert.Empty(await scenario.Db.OutboxMessages.AsNoTracking()
            .Where(message => message.SessionId == scenario.SessionId).ToListAsync());
        Assert.Empty(await scenario.Db.TelemetryCommands.AsNoTracking()
            .Where(command => command.SessionId == scenario.SessionId).ToListAsync());
    }

    [Fact]
    public async Task IncompletePhysicalClosureRequiresScopedAuditedConfirmationBeforeItCanBeResolved()
    {
        await using var scenario = await TestScenario.CreateWithoutTransactionAsync(
            MachineSessionStates.ClosureIncomplete);
        var session = await scenario.Db.MachineSessions.SingleAsync(item => item.Id == scenario.SessionId);
        session.ClosedAt = DateTime.UtcNow.AddMinutes(-1);
        await scenario.Db.SaveChangesAsync();
        var userId = Guid.NewGuid();

        await Assert.ThrowsAsync<KeyNotFoundException>(() => scenario.Orchestrator.ResolveIncompleteClosureAsync(
            scenario.SessionId, Guid.NewGuid(), userId, "SUP-1234", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.Orchestrator.ResolveIncompleteClosureAsync(
            scenario.SessionId, scenario.CompanyId, userId, "invalid ref with spaces", CancellationToken.None));
        var unresolved = await scenario.ReloadSessionAsync();
        Assert.Equal(MachineSessionStates.ClosureIncomplete, unresolved.State);
        Assert.Empty(await scenario.Db.TelemetryEvents.AsNoTracking()
            .Where(item => item.SessionId == scenario.SessionId && item.EventType == "session.closure_verified")
            .ToListAsync());

        await scenario.Orchestrator.ResolveIncompleteClosureAsync(
            scenario.SessionId, scenario.CompanyId, userId, "SUP-1234", CancellationToken.None);

        var resolved = await scenario.ReloadSessionAsync();
        var resolutionEvent = await scenario.SingleEventAsync("session.closure_verified");
        Assert.Equal(MachineSessionStates.ClosureVerified, resolved.State);
        Assert.NotNull(resolved.ClosedAt);
        Assert.Equal(userId, resolutionEvent.UserId);
        Assert.Contains("SUP-1234", resolutionEvent.DataJson);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.Orchestrator.ResolveIncompleteClosureAsync(
            scenario.SessionId, scenario.CompanyId, userId, "SUP-1234", CancellationToken.None));
        Assert.Equal(1, await scenario.EventCountAsync("session.closure_verified"));
    }

    [Fact]
    public async Task ProductionSessionRejectsMockProviderCredentialsInsteadOfCreatingMockPix()
    {
        await using var scenario = await TestScenario.CreateWithoutTransactionAsync(
            MachineSessionStates.AwaitingSelection,
            environmentName: Environments.Production,
            addMockIntegration: true);

        await scenario.SendRawAsync(new
        {
            command = "vend",
            type = "request",
            itemPrice = 200,
            itemNumber = 1
        });

        var transaction = await scenario.Db.PaymentTransactions.SingleAsync();
        Assert.Equal("Failed", transaction.Status);
        Assert.Null(transaction.MercadoPagoPaymentId);
        Assert.Empty(transaction.QrCode);
    }

    [Theory]
    [InlineData(0, 200)]
    [InlineData(100, 200)]
    [InlineData(5, 1)]
    [InlineData(99.9, 1)]
    public async Task InvalidEffectiveApplicationFeeDeniesSaleWithoutCreatingPayment(
        decimal feePercent,
        int amountCents)
    {
        await using var scenario = await TestScenario.CreateWithoutTransactionAsync(
            MachineSessionStates.AwaitingSelection);
        scenario.Db.SystemSettings.Add(new SystemSettings { ApplicationFeePercent = feePercent });
        await scenario.Db.SaveChangesAsync();

        await scenario.SendRawAsync(new
        {
            command = "vend",
            type = "request",
            itemPrice = amountCents,
            itemNumber = 1
        });

        var session = await scenario.ReloadSessionAsync();
        Assert.Equal(MachineSessionStates.DeniedAwaitingClosure, session.State);
        Assert.Equal("invalid_application_fee", session.CloseReason);
        Assert.Null(session.TransactionId);
        Assert.Empty(await scenario.Db.PaymentTransactions.AsNoTracking().ToListAsync());
        Assert.Equal(0, scenario.ProviderRequestCount);
        Assert.Contains(await scenario.Db.TelemetryCommands.AsNoTracking().ToListAsync(), command =>
            command.SessionId == scenario.SessionId && command.Command == "VENDA_NEGADA");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellingPendingPix_CancelsPaymentAndAllowsClosure(bool includeTransactionId)
    {
        await using var scenario = await TestScenario.CreatePaidAsync(
            MachineSessionStates.PaymentPending,
            transactionStatus: "Pending");

        await scenario.Orchestrator.CancelSessionAsync(
            scenario.SessionId,
            scenario.CompanyId,
            Guid.NewGuid(),
            "user_cancelled",
            CancellationToken.None);
        await scenario.SendAsync("pool", "vend_denied", includeTransactionId);
        await scenario.CloseFromDeviceAsync(includeTransactionId);

        var pending = await scenario.ReloadSessionAsync();
        Assert.Equal("CancellationPending", pending.Transaction!.Status);
        await scenario.Orchestrator.ProcessOutboxAsync(CancellationToken.None);

        var session = await scenario.ReloadSessionAsync();
        Assert.Equal(MachineSessionStates.Denied, session.State);
        Assert.Equal("Cancelled", session.Transaction!.Status);
        Assert.NotNull(session.Transaction.CompletedAt);
        Assert.Equal("user_cancelled", session.CloseReason);
        Assert.NotNull(session.ClosedAt);
        Assert.Equal(1, await scenario.EventCountAsync("payment.cancelled"));
        Assert.Equal(1, await scenario.OutboxCountAsync("cancel_payment"));
    }

    [Fact]
    public async Task ClosingWithoutSelection_DoesNotRequireTransactionId()
    {
        await using var scenario = await TestScenario.CreateWithoutTransactionAsync(MachineSessionStates.AwaitingSelection);

        await scenario.SendAsync("vend", "sessioncomplete", includeTransactionId: false);
        await scenario.SendAsync("pool", "end_session", includeTransactionId: false);

        var session = await scenario.ReloadSessionAsync();
        Assert.Equal(MachineSessionStates.ClosedByDeviceBeforeSelection, session.State);
        Assert.Equal("device_closed_before_selection", session.CloseReason);
        Assert.NotNull(session.ClosedAt);
        Assert.Equal(0, await scenario.EventCountAsync("refund.requested"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EndSessionBeforeDeliveryResult_ClosesAndRefunds(bool includeTransactionId)
    {
        await using var scenario = await TestScenario.CreatePaidAsync(MachineSessionStates.AwaitingDeliveryResult);

        await scenario.SendAsync("pool", "end_session", includeTransactionId);

        var session = await scenario.ReloadSessionAsync();
        Assert.Equal(MachineSessionStates.Refunded, session.State);
        Assert.Equal("Refunded", session.Transaction!.Status);
        Assert.Equal("delivery_result_missing", session.CloseReason);
        Assert.NotNull(session.ClosedAt);
        Assert.Equal(1, await scenario.EventCountAsync("delivery.result_missing"));
        Assert.Equal(1, await scenario.EventCountAsync("refund.requested"));
        Assert.Equal(1, await scenario.EventCountAsync("refund.completed"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EndSession_ProviderFailureKeepsPhysicalSessionClosedAndRefundPending(bool includeTransactionId)
    {
        await using var scenario = await TestScenario.CreatePaidAsync(MachineSessionStates.AwaitingDeliveryResult);
        await scenario.SetProviderPaymentIdAsync("external-payment-without-integration");

        await scenario.SendAsync("pool", "end_session", includeTransactionId);

        var session = await scenario.ReloadSessionAsync();
        var outbox = await scenario.SingleOutboxAsync("refund_payment");
        Assert.NotNull(session.ClosedAt);
        Assert.Equal(MachineSessionStates.RefundPending, session.State);
        Assert.Equal("RefundPending", session.Transaction!.Status);
        Assert.Equal("pending", outbox.Status);
        Assert.Equal(1, outbox.Attempts);
        Assert.Equal(1, await scenario.EventCountAsync("refund.requested"));
        Assert.Equal(0, await scenario.EventCountAsync("refund.completed"));
    }

    [Fact]
    public async Task LegacyApiOverlap_ProcessesCandidateOwnedDeferredOutboxWithoutCompetingWithLegacyWorker()
    {
        await using var scenario = await TestScenario.CreatePaidAsync(
            MachineSessionStates.AwaitingDeliveryResult,
            legacyApiConfigured: true);
        await scenario.SetProviderPaymentIdAsync("mock_overlap_refund");

        await scenario.SendAsync("pool", "end_session", includeTransactionId: true);
        await scenario.Orchestrator.ProcessOutboxAsync(CancellationToken.None);

        var outbox = await scenario.SingleOutboxAsync("refund_payment");
        Assert.Equal("processed", outbox.Status);
        Assert.Equal(1, outbox.Attempts);
        Assert.Equal(0, scenario.ProviderRequestCount);
        var session = await scenario.ReloadSessionAsync();
        Assert.Equal(MachineSessionStates.Refunded, session.State);
        Assert.Equal("Refunded", session.Transaction!.Status);
        Assert.Equal(1, await scenario.EventCountAsync("refund.completed"));
    }

    [Fact]
    public async Task LegacyApiOverlap_KeepsFailedCandidateOutboxInLegacyIgnoredStatusForRetry()
    {
        await using var scenario = await TestScenario.CreatePaidAsync(
            MachineSessionStates.AwaitingDeliveryResult,
            legacyApiConfigured: true);
        await scenario.SetProviderPaymentIdAsync("external-payment-without-integration");

        await scenario.SendAsync("pool", "end_session", includeTransactionId: true);

        var outbox = await scenario.SingleOutboxAsync("refund_payment");
        Assert.Equal("deferred_legacy_overlap", outbox.Status);
        Assert.Equal("provider_operation_failed", outbox.LastError);
        Assert.Equal(1, outbox.Attempts);
        Assert.True(outbox.NextAttemptAt > DateTime.UtcNow);
        Assert.Equal(0, scenario.ProviderRequestCount);
        var session = await scenario.ReloadSessionAsync();
        Assert.Equal(MachineSessionStates.RefundPending, session.State);
    }

    [Fact]
    public async Task LegacyApiOverlap_ReclaimsExpiredOutboxLeaseWithoutExposingItToLegacyWorker()
    {
        await using var scenario = await TestScenario.CreatePaidAsync(
            MachineSessionStates.AwaitingDeliveryResult,
            legacyApiConfigured: true);
        await scenario.SetProviderPaymentIdAsync("mock_expired_overlap_refund");
        scenario.Db.OutboxMessages.Add(new OutboxMessage
        {
            CompanyId = scenario.CompanyId,
            MachineId = scenario.MachineId,
            SessionId = scenario.SessionId,
            TransactionId = scenario.TransactionId,
            MessageType = "refund_payment",
            PayloadJson = "{}",
            Status = "processing",
            Attempts = 0,
            CreatedAt = DateTime.UtcNow.AddMinutes(-3),
            NextAttemptAt = DateTime.UtcNow.AddMinutes(-1)
        });
        await scenario.Db.SaveChangesAsync();

        await scenario.Orchestrator.ProcessOutboxAsync(CancellationToken.None);

        var outbox = await scenario.SingleOutboxAsync("refund_payment");
        var session = await scenario.ReloadSessionAsync();
        Assert.Equal("processed", outbox.Status);
        Assert.Equal(1, outbox.Attempts);
        Assert.Equal(MachineSessionStates.Refunded, session.State);
        Assert.Equal("Refunded", session.Transaction!.Status);
    }

    [Fact]
    public async Task DeferredFinancialOutbox_ResumesAndCompletesAfterLegacyRelayIsRemoved()
    {
        await using var scenario = await TestScenario.CreatePaidAsync(MachineSessionStates.AwaitingDeliveryResult);
        scenario.Db.OutboxMessages.Add(new OutboxMessage
        {
            CompanyId = scenario.CompanyId,
            MachineId = scenario.MachineId,
            SessionId = scenario.SessionId,
            TransactionId = scenario.TransactionId,
            MessageType = "refund_payment",
            PayloadJson = "{}",
            Status = "deferred_legacy_overlap",
            NextAttemptAt = DateTime.UtcNow
        });
        await scenario.Db.SaveChangesAsync();

        await scenario.Orchestrator.ProcessOutboxAsync(CancellationToken.None);

        var outbox = await scenario.Db.OutboxMessages.AsNoTracking().SingleAsync();
        var session = await scenario.ReloadSessionAsync();
        Assert.Equal("processed", outbox.Status);
        Assert.Equal(MachineSessionStates.Refunded, session.State);
        Assert.Equal("Refunded", session.Transaction!.Status);
        Assert.Equal(1, await scenario.EventCountAsync("refund.completed"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosedSession_FinancialOutboxCanResumeAfterProviderRecovery(bool includeTransactionId)
    {
        await using var scenario = await TestScenario.CreatePaidAsync(MachineSessionStates.AwaitingDeliveryResult);
        await scenario.SetProviderPaymentIdAsync("external-payment-without-integration");
        await scenario.SendAsync("pool", "end_session", includeTransactionId);
        var physicallyClosedAt = (await scenario.ReloadSessionAsync()).ClosedAt;

        await scenario.SetProviderPaymentIdAsync($"mock_{Guid.NewGuid():N}");
        await scenario.MakeOutboxDueAsync("refund_payment");
        await scenario.Orchestrator.ProcessOutboxAsync(CancellationToken.None);

        var session = await scenario.ReloadSessionAsync();
        var outbox = await scenario.SingleOutboxAsync("refund_payment");
        Assert.Equal(physicallyClosedAt, session.ClosedAt);
        Assert.Equal(MachineSessionStates.Refunded, session.State);
        Assert.Equal("Refunded", session.Transaction!.Status);
        Assert.Equal("processed", outbox.Status);
        Assert.Equal(2, outbox.Attempts);
        Assert.Equal(1, await scenario.EventCountAsync("refund.completed"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EndSessionDuringPendingPayment_ClosesBeforeCancellationIsProcessed(bool includeTransactionId)
    {
        await using var scenario = await TestScenario.CreatePaidAsync(
            MachineSessionStates.PaymentPending,
            transactionStatus: "Pending");
        await scenario.SetProviderPaymentIdAsync("external-payment-without-integration");

        await scenario.SendAsync("pool", "end_session", includeTransactionId);

        var session = await scenario.ReloadSessionAsync();
        var outbox = await scenario.SingleOutboxAsync("cancel_payment");
        Assert.NotNull(session.ClosedAt);
        Assert.Equal(MachineSessionStates.Denied, session.State);
        Assert.Equal("CancellationPending", session.Transaction!.Status);
        Assert.Equal("pending", outbox.Status);
        Assert.Equal(0, outbox.Attempts);

        await scenario.Orchestrator.ProcessOutboxAsync(CancellationToken.None);
        session = await scenario.ReloadSessionAsync();
        outbox = await scenario.SingleOutboxAsync("cancel_payment");
        Assert.NotNull(session.ClosedAt);
        Assert.Equal("CancellationPending", session.Transaction!.Status);
        Assert.Equal(1, outbox.Attempts);
    }

    [Fact]
    public async Task RefundFailureAfterFiveAttempts_RequiresReconciliationButStaysClosed()
    {
        await using var scenario = await TestScenario.CreatePaidAsync(MachineSessionStates.AwaitingDeliveryResult);
        await scenario.SetProviderPaymentIdAsync("external-payment-without-integration");
        await scenario.SendAsync("pool", "end_session", includeTransactionId: true);

        for (var attempt = 1; attempt < 5; attempt++)
        {
            await scenario.MakeOutboxDueAsync("refund_payment");
            await scenario.Orchestrator.ProcessOutboxAsync(CancellationToken.None);
        }

        var session = await scenario.ReloadSessionAsync();
        var outbox = await scenario.SingleOutboxAsync("refund_payment");
        Assert.NotNull(session.ClosedAt);
        Assert.Equal(MachineSessionStates.ReconciliationRequired, session.State);
        Assert.Equal("ReconciliationRequired", session.Transaction!.Status);
        Assert.Equal("failed", outbox.Status);
        Assert.Equal(5, outbox.Attempts);
        Assert.Equal(1, await scenario.EventCountAsync("refund.failed"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicateEndSession_DoesNotDuplicateFinancialWork(bool includeTransactionId)
    {
        await using var scenario = await TestScenario.CreatePaidAsync(MachineSessionStates.AwaitingDeliveryResult);

        await scenario.SendAsync("pool", "end_session", includeTransactionId);
        await scenario.SendAsync("pool", "end_session", includeTransactionId);

        Assert.NotNull((await scenario.ReloadSessionAsync()).ClosedAt);
        Assert.Equal(1, await scenario.OutboxCountAsync("refund_payment"));
        Assert.Equal(1, await scenario.EventCountAsync("refund.requested"));
        Assert.Equal(1, await scenario.EventCountAsync("refund.completed"));
    }

    [Theory]
    [InlineData("vend", "failure")]
    [InlineData("pool", "end_session")]
    public async Task DivergentTransactionId_IsRejectedWithoutChangingState(string command, string type)
    {
        await using var scenario = await TestScenario.CreatePaidAsync(MachineSessionStates.AwaitingDeliveryResult);

        await scenario.SendAsync(
            command,
            type,
            includeTransactionId: true,
            reason: "mechanism_error",
            transactionIdOverride: Guid.NewGuid().ToString());

        var session = await scenario.ReloadSessionAsync();
        Assert.Equal(MachineSessionStates.AwaitingDeliveryResult, session.State);
        Assert.NotNull(session.DeliveryDeadlineAt);
        Assert.Equal("Approved", session.Transaction!.Status);
        Assert.Equal(1, await scenario.EventCountAsync("protocol.transaction_mismatch"));
        Assert.Equal(0, await scenario.EventCountAsync("refund.requested"));
    }

    [Theory]
    [InlineData("vend", "failure")]
    [InlineData("pool", "end_session")]
    public async Task InvalidTransactionId_IsRejectedWithoutChangingState(string command, string type)
    {
        await using var scenario = await TestScenario.CreatePaidAsync(MachineSessionStates.AwaitingDeliveryResult);

        await scenario.SendAsync(
            command,
            type,
            includeTransactionId: true,
            reason: "mechanism_error",
            transactionIdOverride: "transaction-id-invalido");

        var session = await scenario.ReloadSessionAsync();
        Assert.Equal(MachineSessionStates.AwaitingDeliveryResult, session.State);
        Assert.Equal("Approved", session.Transaction!.Status);
        Assert.Equal(1, await scenario.EventCountAsync("protocol.invalid_transaction_id"));
        Assert.Equal(0, await scenario.EventCountAsync("refund.requested"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicateFailure_IsIdempotent(bool includeTransactionId)
    {
        await using var scenario = await TestScenario.CreatePaidAsync(MachineSessionStates.AwaitingDeliveryResult);

        await scenario.SendAsync("vend", "failure", includeTransactionId, reason: "out_of_product");
        await scenario.SendAsync("vend", "failure", includeTransactionId, reason: "out_of_product");

        var session = await scenario.ReloadSessionAsync();
        Assert.Equal(MachineSessionStates.Refunded, session.State);
        Assert.Equal(1, await scenario.Db.DeliveryFailures.AsNoTracking().CountAsync());
        Assert.Equal(1, await scenario.EventCountAsync("refund.requested"));
        Assert.Equal(1, await scenario.EventCountAsync("refund.completed"));
        Assert.Equal(1, await scenario.OutboxCountAsync("refund_payment"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicateSuccess_IsIdempotent(bool includeTransactionId)
    {
        await using var scenario = await TestScenario.CreatePaidAsync(MachineSessionStates.AwaitingDeliveryResult);

        await scenario.SendAsync("vend", "success", includeTransactionId);
        await scenario.SendAsync("vend", "success", includeTransactionId);

        var session = await scenario.ReloadSessionAsync();
        Assert.Equal(MachineSessionStates.Completed, session.State);
        Assert.Equal("Approved", session.Transaction!.Status);
        Assert.Equal(1, await scenario.EventCountAsync("delivery.success"));
        Assert.Equal(0, await scenario.EventCountAsync("refund.requested"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessAfterRefund_IsRecordedAsConflict(bool includeTransactionId)
    {
        await using var scenario = await TestScenario.CreatePaidAsync(MachineSessionStates.AwaitingDeliveryResult);
        scenario.Db.ProductTypes.Add(new ProductType
        {
            Id = "drinks",
            Name = "Bebidas",
            CompanyId = scenario.CompanyId
        });
        scenario.Db.Products.Add(new FullProduct
        {
            Id = "water-500ml",
            Code = "W500",
            Name = "Água 500 ml",
            TypeId = "drinks",
            CompanyId = scenario.CompanyId
        });
        scenario.Db.MachineProductSlots.Add(new MachineProductSlot
        {
            CompanyId = scenario.CompanyId,
            MachineId = scenario.MachineId,
            ItemNumber = 2,
            ProductId = "water-500ml",
            Capacity = 10,
            CurrentStock = 3,
            LowStockThreshold = 2
        });
        await scenario.Db.SaveChangesAsync();

        await scenario.SendAsync("vend", "failure", includeTransactionId, reason: "out_of_cup");
        await scenario.SendAsync("vend", "success", includeTransactionId);

        var session = await scenario.ReloadSessionAsync();
        Assert.Equal(MachineSessionStates.Refunded, session.State);
        Assert.Equal("Refunded", session.Transaction!.Status);
        Assert.Equal(1, await scenario.EventCountAsync("delivery.conflicting_result"));
        Assert.Equal(1, await scenario.EventCountAsync("refund.requested"));

        var sale = await scenario.Db.SaleRecords.AsNoTracking().SingleAsync();
        var slot = await scenario.Db.MachineProductSlots.AsNoTracking().SingleAsync();
        var movement = await scenario.Db.InventoryMovements.AsNoTracking().SingleAsync();
        Assert.Equal("Refunded", sale.FinancialStatus);
        Assert.Equal(2m, sale.RefundedAmount);
        Assert.Equal(1, sale.Quantity);
        Assert.Equal(2, slot.CurrentStock);
        Assert.Equal(-1, movement.QuantityDelta);

        await scenario.SendAsync("vend", "success", includeTransactionId);
        Assert.Single(await scenario.Db.SaleRecords.AsNoTracking().ToListAsync());
        Assert.Single(await scenario.Db.InventoryMovements.AsNoTracking().ToListAsync());
        Assert.Equal(2, (await scenario.Db.MachineProductSlots.AsNoTracking().SingleAsync()).CurrentStock);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureAfterSuccess_IsRecordedAsConflictWithoutRefund(bool includeTransactionId)
    {
        await using var scenario = await TestScenario.CreatePaidAsync(MachineSessionStates.AwaitingDeliveryResult);

        await scenario.SendAsync("vend", "success", includeTransactionId);
        await scenario.SendAsync("vend", "failure", includeTransactionId, reason: "mechanism_error");

        var session = await scenario.ReloadSessionAsync();
        Assert.Equal(MachineSessionStates.Completed, session.State);
        Assert.Equal("Approved", session.Transaction!.Status);
        Assert.Equal(1, await scenario.EventCountAsync("delivery.conflicting_result"));
        Assert.Equal(0, await scenario.EventCountAsync("refund.requested"));
        Assert.Equal(0, await scenario.Db.DeliveryFailures.AsNoTracking().CountAsync());
    }

    [Theory]
    [InlineData(MdbStatuses.Inactive)]
    [InlineData(MdbStatuses.Disabled)]
    [InlineData(MdbStatuses.Enabled)]
    [InlineData(MdbStatuses.Idle)]
    [InlineData(MdbStatuses.Vend)]
    public async Task MdbStatusWithoutDataType_IsPersistedWithoutChangingActiveSession(string mdbStatus)
    {
        await using var scenario = await TestScenario.CreateWithoutTransactionAsync(MachineSessionStates.AwaitingSelection);

        await scenario.SendRawAsync(new { command = "status", status = mdbStatus });

        scenario.Db.ChangeTracker.Clear();
        var connection = await scenario.Db.MachineConnectionStates.AsNoTracking()
            .SingleAsync(x => x.MachineId == scenario.MachineId);
        var session = await scenario.ReloadSessionAsync();

        Assert.Equal(mdbStatus, connection.MdbStatus);
        Assert.NotNull(connection.MdbStatusUpdatedAt);
        Assert.Contains(mdbStatus, connection.MdbStatusRaw);
        Assert.Equal(MachineSessionStates.AwaitingSelection, session.State);
        Assert.Equal(1, await scenario.Db.TelemetryEvents.AsNoTracking()
            .CountAsync(x => x.MachineId == scenario.MachineId && x.EventType == "mdb.status.changed"));
    }

    [Fact]
    public async Task UnknownMdbStatus_BecomesUnavailableAndDoesNotOpenOrCloseSession()
    {
        await using var scenario = await TestScenario.CreateWithoutTransactionAsync(MachineSessionStates.AwaitingSelection);

        await scenario.SendRawAsync(new { command = "status", status = "unexpected_state" });
        await scenario.SendRawAsync(new { command = "status", status = "unexpected_state" });

        scenario.Db.ChangeTracker.Clear();
        var connection = await scenario.Db.MachineConnectionStates.AsNoTracking()
            .SingleAsync(x => x.MachineId == scenario.MachineId);
        var session = await scenario.ReloadSessionAsync();

        Assert.Null(connection.MdbStatus);
        Assert.NotNull(connection.MdbStatusUpdatedAt);
        Assert.Equal(MachineSessionStates.AwaitingSelection, session.State);
        Assert.Equal(1, await scenario.Db.TelemetryEvents.AsNoTracking()
            .CountAsync(x => x.MachineId == scenario.MachineId && x.EventType == "mdb.status.invalid"));
    }

    [Fact]
    public async Task SuccessfulDelivery_RecordsOneSaleAndDecrementsMappedStockOnce()
    {
        await using var scenario = await TestScenario.CreatePaidAsync(MachineSessionStates.AwaitingDeliveryResult);
        scenario.Db.ProductTypes.Add(new ProductType
        {
            Id = "drinks",
            Name = "Bebidas",
            CompanyId = scenario.CompanyId
        });
        scenario.Db.Products.Add(new FullProduct
        {
            Id = "water-500ml",
            Code = "W500",
            Name = "Água 500 ml",
            TypeId = "drinks",
            CompanyId = scenario.CompanyId,
            Cost = 1.25m
        });
        scenario.Db.MachineProductSlots.Add(new MachineProductSlot
        {
            CompanyId = scenario.CompanyId,
            MachineId = scenario.MachineId,
            ItemNumber = 2,
            ProductId = "water-500ml",
            Capacity = 10,
            CurrentStock = 3,
            LowStockThreshold = 2
        });
        await scenario.Db.SaveChangesAsync();

        await scenario.SendAsync("vend", "success", includeTransactionId: true);
        await scenario.SendAsync("vend", "success", includeTransactionId: true);

        var sale = await scenario.Db.SaleRecords.AsNoTracking().SingleAsync();
        var slot = await scenario.Db.MachineProductSlots.AsNoTracking().SingleAsync();
        var movement = await scenario.Db.InventoryMovements.AsNoTracking().SingleAsync();
        Assert.Equal(scenario.TransactionId, sale.TransactionId);
        Assert.Equal("water-500ml", sale.ProductId);
        Assert.Equal("Água 500 ml", sale.ProductNameSnapshot);
        Assert.Equal(2m, sale.GrossAmount);
        Assert.Equal(1, sale.Quantity);
        Assert.Equal(2, slot.CurrentStock);
        Assert.Equal(-1, movement.QuantityDelta);
        Assert.Equal(2, movement.QuantityAfter);
    }

    [Fact]
    public async Task SuccessfulDeliveryDoesNotSnapshotProductNameFromAnotherCompany()
    {
        await using var scenario = await TestScenario.CreatePaidAsync(MachineSessionStates.AwaitingDeliveryResult);
        var foreignCompanyId = Guid.NewGuid();
        scenario.Db.Companies.Add(new Company
        {
            Id = foreignCompanyId,
            Name = "Empresa estrangeira",
            Cnpj = "22222222000122"
        });
        scenario.Db.ProductTypes.Add(new ProductType
        {
            Id = "foreign-drinks",
            Name = "Bebidas estrangeiras",
            CompanyId = foreignCompanyId
        });
        scenario.Db.Products.Add(new FullProduct
        {
            Id = "foreign-water",
            Code = "FW500",
            Name = "Produto confidencial de outra empresa",
            TypeId = "foreign-drinks",
            CompanyId = foreignCompanyId
        });
        scenario.Db.MachineProductSlots.Add(new MachineProductSlot
        {
            CompanyId = scenario.CompanyId,
            MachineId = scenario.MachineId,
            ItemNumber = 2,
            ProductId = "foreign-water",
            Capacity = 10,
            CurrentStock = 3,
            LowStockThreshold = 2
        });
        await scenario.Db.SaveChangesAsync();

        await scenario.SendAsync("vend", "success", includeTransactionId: true);

        var sale = await scenario.Db.SaleRecords.AsNoTracking().SingleAsync();
        Assert.Null(sale.ProductNameSnapshot);
        Assert.Equal("foreign-water", sale.ProductId);
    }

    [Fact]
    public async Task DeviceMessageFromNewMachineOwnerDoesNotAdvancePreviousOwnersSession()
    {
        await using var scenario = await TestScenario.CreatePaidAsync(MachineSessionStates.AwaitingDeliveryResult);
        var newCompanyId = Guid.NewGuid();
        scenario.Db.Companies.Add(new Company
        {
            Id = newCompanyId,
            Name = "Nova empresa proprietária",
            Cnpj = "33333333000133"
        });
        var machine = await scenario.Db.Machines.SingleAsync(item => item.Id == scenario.MachineId);
        machine.CompanyId = newCompanyId;
        await scenario.Db.SaveChangesAsync();

        var data = JsonSerializer.Serialize(new
        {
            command = "vend",
            type = "success",
            transactionId = scenario.TransactionId
        });
        await scenario.Orchestrator.HandleDeviceMessageAsync(
            new InboundDeviceMessage(
                scenario.MachineId,
                newCompanyId,
                machine.SerialNumber,
                "new-owner-connection",
                991,
                data,
                DateTime.UtcNow),
            CancellationToken.None);

        var session = await scenario.ReloadSessionAsync();
        Assert.Equal(MachineSessionStates.AwaitingDeliveryResult, session.State);
        Assert.Empty(await scenario.Db.SaleRecords.AsNoTracking().ToListAsync());
        Assert.Contains(await scenario.Db.TelemetryEvents.AsNoTracking().ToListAsync(), item =>
            item.CompanyId == newCompanyId && item.MachineId == scenario.MachineId && item.SessionId == null);
    }

    [Fact]
    public async Task OutboxWithSessionFromAnotherCompanyDoesNotCallProviderOrChangeEitherSession()
    {
        await using var scenario = await TestScenario.CreatePaidAsync(MachineSessionStates.AwaitingDeliveryResult);
        await scenario.SetProviderPaymentIdAsync("external-payment-for-outbox-test");
        scenario.Db.MercadoPagoIntegrations.Add(new MercadoPagoIntegration
        {
            CompanyId = scenario.CompanyId,
            IsActive = true,
            AccessToken = EncryptionService.Encrypt("test-access-token"),
            RefreshToken = string.Empty,
            ClientSecret = string.Empty
        });
        await scenario.Db.SaveChangesAsync();

        var foreignCompanyId = Guid.NewGuid();
        var foreignMachine = new Machine
        {
            Id = "foreign-outbox-machine",
            Name = "Máquina de outra empresa",
            SerialNumber = "FOREIGN-OUTBOX-SERIAL",
            CompanyId = foreignCompanyId
        };
        var foreignSession = new MachineSession
        {
            Id = Guid.NewGuid(),
            MachineId = foreignMachine.Id,
            CompanyId = foreignCompanyId,
            State = MachineSessionStates.Completed,
            ClosedAt = DateTime.UtcNow
        };
        scenario.Db.Companies.Add(new Company
        {
            Id = foreignCompanyId,
            Name = "Empresa estrangeira",
            Cnpj = "44444444000144"
        });
        scenario.Db.Machines.Add(foreignMachine);
        scenario.Db.MachineSessions.Add(foreignSession);
        var outbox = new OutboxMessage
        {
            CompanyId = scenario.CompanyId,
            MachineId = scenario.MachineId,
            SessionId = foreignSession.Id,
            TransactionId = scenario.TransactionId,
            MessageType = "refund_payment",
            Status = "pending",
            NextAttemptAt = DateTime.UtcNow.AddMinutes(-1)
        };
        scenario.Db.OutboxMessages.Add(outbox);
        await scenario.Db.SaveChangesAsync();
        var outboxId = outbox.Id;

        await scenario.Orchestrator.ProcessOutboxAsync(CancellationToken.None);

        outbox = await scenario.Db.OutboxMessages.AsNoTracking().SingleAsync(message => message.Id == outboxId);
        var originalSession = await scenario.ReloadSessionAsync();
        var unchangedForeignSession = await scenario.Db.MachineSessions.AsNoTracking()
            .SingleAsync(session => session.Id == foreignSession.Id);
        Assert.Equal("failed", outbox.Status);
        Assert.Equal("association_mismatch", outbox.LastError);
        Assert.Equal(0, outbox.Attempts);
        Assert.Equal(0, scenario.ProviderRequestCount);
        Assert.Equal(MachineSessionStates.AwaitingDeliveryResult, originalSession.State);
        Assert.Equal("Approved", originalSession.Transaction!.Status);
        Assert.Equal(MachineSessionStates.Completed, unchangedForeignSession.State);
        Assert.DoesNotContain(await scenario.Db.TelemetryEvents.AsNoTracking().ToListAsync(),
            item => item.SessionId == foreignSession.Id && item.EventType == "refund.completed");
    }

    [Fact]
    public async Task Database_AllowsOnlyOneOpenSessionPerMachine()
    {
        await using var scenario = await TestScenario.CreateWithoutTransactionAsync(MachineSessionStates.AwaitingSelection);
        scenario.Db.MachineSessions.Add(new MachineSession
        {
            MachineId = scenario.MachineId,
            CompanyId = scenario.CompanyId,
            State = MachineSessionStates.Opening
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => scenario.Db.SaveChangesAsync());
    }

    private sealed class TestScenario : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _services;
        private readonly TestHttpClientFactory _httpClientFactory;
        private int _messageId;

        private TestScenario(
            SqliteConnection connection,
            ServiceProvider services,
            AppDbContext db,
            SessionOrchestrator orchestrator,
            Guid companyId,
            string machineId,
            Guid sessionId,
            Guid? transactionId,
            TestHttpClientFactory httpClientFactory)
        {
            _connection = connection;
            _services = services;
            Db = db;
            Orchestrator = orchestrator;
            CompanyId = companyId;
            MachineId = machineId;
            SessionId = sessionId;
            TransactionId = transactionId ?? Guid.Empty;
            _httpClientFactory = httpClientFactory;
        }

        public AppDbContext Db { get; }
        public SessionOrchestrator Orchestrator { get; }
        public Guid CompanyId { get; }
        public string MachineId { get; }
        public Guid SessionId { get; }
        public Guid TransactionId { get; }
        public int ProviderRequestCount => _httpClientFactory.RequestCount;

        public static Task<TestScenario> CreatePaidAsync(
            string state,
            string transactionStatus = "Approved",
            bool legacyApiConfigured = false) =>
            CreateAsync(state, includeTransaction: true, transactionStatus, legacyApiConfigured: legacyApiConfigured);

        public static Task<TestScenario> CreateWithoutTransactionAsync(
            string state,
            string? environmentName = null,
            bool addMockIntegration = false,
            IHostApplicationLifetime? lifetime = null,
            bool legacyApiConfigured = false,
            ITelemetryConnectionOperations? connections = null) =>
            CreateAsync(
                state,
                includeTransaction: false,
                transactionStatus: "Pending",
                environmentName: environmentName ?? Environments.Development,
                addMockIntegration: addMockIntegration,
                lifetime: lifetime,
                legacyApiConfigured: legacyApiConfigured,
                connections: connections);

        private static async Task<TestScenario> CreateAsync(
            string state,
            bool includeTransaction,
            string transactionStatus,
            string? environmentName = null,
            bool addMockIntegration = false,
            IHostApplicationLifetime? lifetime = null,
            bool legacyApiConfigured = false,
            ITelemetryConnectionOperations? connections = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new AppDbContext(options);
            await db.Database.EnsureCreatedAsync();
            db.SessionAdmissionGates.Add(new SessionAdmissionGate
            {
                Id = SessionAdmissionGate.SingletonId,
                NewSessionsPaused = false,
                UpdatedBy = "test",
                Reason = "session fixture"
            });
            var services = new ServiceCollection()
                .AddDbContext<AppDbContext>(builder => builder.UseSqlite(connection))
                .BuildServiceProvider();

            var company = new Company
            {
                Id = Guid.NewGuid(),
                Name = "Empresa de teste",
                Cnpj = "00000000000000",
                CreatedByUserId = Guid.NewGuid()
            };
            var serial = $"SERIAL-{Guid.NewGuid():N}";
            var machine = new Machine
            {
                Id = $"machine-{Guid.NewGuid():N}",
                Name = "Máquina de teste",
                SerialNumber = serial,
                NormalizedSerialNumber = serial.ToUpperInvariant(),
                CompanyId = company.Id,
                MercadoPagoEnabled = true
            };

            PaymentTransaction? transaction = null;
            if (includeTransaction)
            {
                transaction = new PaymentTransaction
                {
                    Id = Guid.NewGuid(),
                    MachineId = machine.Id,
                    CompanyId = company.Id,
                    Amount = 2m,
                    Status = transactionStatus,
                    MercadoPagoPaymentId = $"mock_{Guid.NewGuid():N}"
                };
            }

            var session = new MachineSession
            {
                Id = Guid.NewGuid(),
                MachineId = machine.Id,
                CompanyId = company.Id,
                State = state,
                ItemNumber = includeTransaction ? 2 : null,
                AmountCents = includeTransaction ? 200 : null,
                TransactionId = transaction?.Id,
                SelectionDeadlineAt = state == MachineSessionStates.AwaitingSelection
                    ? DateTime.UtcNow.AddMinutes(2)
                    : null,
                PaymentDeadlineAt = state == MachineSessionStates.PaymentPending
                    ? DateTime.UtcNow.AddMinutes(5)
                    : null,
                DeliveryDeadlineAt = state is MachineSessionStates.PaymentApproved or MachineSessionStates.AwaitingDeliveryResult
                    ? DateTime.UtcNow.AddMinutes(1)
                    : null
            };

            db.Companies.Add(company);
            db.Machines.Add(machine);
            if (addMockIntegration)
            {
                db.MercadoPagoIntegrations.Add(new MercadoPagoIntegration
                {
                    CompanyId = company.Id,
                    IsActive = true,
                    AccessToken = "mock_access_token",
                    RefreshToken = string.Empty,
                    ClientSecret = string.Empty
                });
                db.SystemSettings.Add(new SystemSettings { ApplicationFeePercent = 5m });
            }
            if (transaction != null) db.PaymentTransactions.Add(transaction);
            db.MachineSessions.Add(session);
            await db.SaveChangesAsync();

            var panelHub = new TelemetryPanelHub(NullLogger<TelemetryPanelHub>.Instance);
            var connectionManager = new TelemetryConnectionManager(
                services.GetRequiredService<IServiceScopeFactory>(),
                panelHub,
                NullLogger<TelemetryConnectionManager>.Instance,
                lifetime: lifetime);
            var httpClientFactory = new TestHttpClientFactory();
            var orchestrator = new SessionOrchestrator(
                db,
                connections ?? connectionManager,
                panelHub,
                httpClientFactory,
                new ConfigurationBuilder()
                    .AddInMemoryCollection(legacyApiConfigured
                        ? new Dictionary<string, string?> { ["Telemetry:LegacyApiBaseUrl"] = "http://legacy-api.internal" }
                        : new Dictionary<string, string?>())
                    .Build(),
                new TestWebHostEnvironment(environmentName ?? Environments.Development),
                NullLogger<SessionOrchestrator>.Instance);

            return new TestScenario(
                connection,
                services,
                db,
                orchestrator,
                company.Id,
                machine.Id,
                session.Id,
                transaction?.Id,
                httpClientFactory);
        }

        public async Task SendAsync(
            string command,
            string type,
            bool includeTransactionId,
            string? reason = null,
            string? transactionIdOverride = null)
        {
            var data = new Dictionary<string, object?>
            {
                ["command"] = command,
                ["type"] = type
            };
            if (includeTransactionId)
                data["transactionId"] = transactionIdOverride ?? TransactionId.ToString();
            if (reason != null)
                data["reason"] = reason;

            var messageId = Interlocked.Increment(ref _messageId);
            await Orchestrator.HandleDeviceMessageAsync(
                new InboundDeviceMessage(
                    MachineId,
                    CompanyId,
                    "SERIAL-TEST",
                    "connection-test",
                    messageId,
                    JsonSerializer.Serialize(data),
                    DateTime.UtcNow.AddTicks(messageId)),
                CancellationToken.None);
        }

        public async Task SendRawAsync(object data)
        {
            var messageId = Interlocked.Increment(ref _messageId);
            await Orchestrator.HandleDeviceMessageAsync(
                new InboundDeviceMessage(
                    MachineId,
                    CompanyId,
                    "SERIAL-TEST",
                    "connection-test",
                    messageId,
                    JsonSerializer.Serialize(data),
                    DateTime.UtcNow.AddTicks(messageId)),
                CancellationToken.None);
        }

        public async Task CloseFromDeviceAsync(bool includeTransactionId)
        {
            await SendAsync("vend", "sessioncomplete", includeTransactionId);
            await SendAsync("pool", "end_session", includeTransactionId);
        }

        public async Task<MachineSession> ReloadSessionAsync()
        {
            Db.ChangeTracker.Clear();
            return await Db.MachineSessions
                .AsNoTracking()
                .Include(x => x.Transaction)
                .SingleAsync(x => x.Id == SessionId);
        }

        public Task<int> EventCountAsync(string eventType) =>
            Db.TelemetryEvents.AsNoTracking().CountAsync(x => x.SessionId == SessionId && x.EventType == eventType);

        public Task<TelemetryEvent> SingleEventAsync(string eventType) =>
            Db.TelemetryEvents.AsNoTracking().SingleAsync(x => x.SessionId == SessionId && x.EventType == eventType);

        public Task<int> OutboxCountAsync(string messageType) =>
            Db.OutboxMessages.AsNoTracking().CountAsync(x => x.SessionId == SessionId && x.MessageType == messageType);

        public Task<OutboxMessage> SingleOutboxAsync(string messageType) =>
            Db.OutboxMessages.AsNoTracking().SingleAsync(x => x.SessionId == SessionId && x.MessageType == messageType);

        public async Task SetProviderPaymentIdAsync(string paymentId)
        {
            var transaction = await Db.PaymentTransactions.SingleAsync(x => x.Id == TransactionId);
            transaction.MercadoPagoPaymentId = paymentId;
            await Db.SaveChangesAsync();
        }

        public async Task MakeOutboxDueAsync(string messageType)
        {
            var outbox = await Db.OutboxMessages.SingleAsync(
                x => x.SessionId == SessionId && x.MessageType == messageType);
            outbox.NextAttemptAt = DateTime.UtcNow.AddSeconds(-1);
            await Db.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class StartConnections : ITelemetryConnectionOperations
    {
        public string NodeId => "test";
        public string Status { get; set; } = "idle_state";
        public bool StatusSuccess { get; set; } = true;
        public bool Stale { get; set; }
        public bool Uncorrelated { get; set; }
        public int OpenCommands { get; private set; }
        public Func<Task>? BeforeStatus { get; set; }
        public IDisposable TryBeginSessionStart() => new NoopDisposable();
        public bool IsLocalConnectionOnline(string serial, string? expectedConnectionId) => true;
        public Task<bool> IsOnlineAsync(string serial, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> HasCommandRouteAsync(string serial, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<HashSet<string>> GetOnlineSerialsAsync(IEnumerable<string> serials, CancellationToken cancellationToken = default) => Task.FromResult(serials.ToHashSet());
        public Task<DeviceCommandResult> DisconnectMachineAsync(string serial, string reason, CancellationToken cancellationToken = default) => Task.FromResult(new DeviceCommandResult(true, 1, 1));
        public Task<DeviceCommandResult> SendCommandAsync(string serial, object data, CancellationToken cancellationToken = default)
        {
            if (data is string text && text == "ABRIR_SESSAO") OpenCommands++;
            return Task.FromResult(new DeviceCommandResult(true, 1, 1));
        }
        public async Task<DeviceStatusResult> SendStatusRequestAsync(string serial, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            if (BeforeStatus != null) await BeforeStatus();
            return new DeviceStatusResult(StatusSuccess, Uncorrelated ? 0 : 2, Status,
                "{\"command\":\"status\",\"status\":\"idle_state\"}", Stale ? DateTime.UtcNow.AddMinutes(-1) : DateTime.UtcNow);
        }
        private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        public HttpClient CreateClient(string name) => new(new RejectExternalRequestsHandler(
            () => Interlocked.Increment(ref _requestCount)));
    }

    private sealed class RejectExternalRequestsHandler(Action onRequest) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onRequest();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }

    private sealed class TestWebHostEnvironment(string environmentName) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "VendingMachines.Api.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = environmentName;
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
