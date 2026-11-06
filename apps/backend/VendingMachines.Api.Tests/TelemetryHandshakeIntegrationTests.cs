using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;
using VendingMachines.Simulator;
using Xunit;

namespace VendingMachines.Api.Tests;

[CollectionDefinition("Telemetry WebSocket resource tests", DisableParallelization = true)]
public sealed class TelemetryWebSocketResourceTestCollection { }

[Collection("Telemetry WebSocket resource tests")]
public sealed class TelemetryHandshakeIntegrationTests
{
    [Fact]
    public async Task AuthenticatedReconnectRepairsStateOwnedByAnotherCompany()
    {
        await using var scenario = await CreateScenarioAsync(requireCredentials: true, revokedAt: null);
        var machine = await scenario.Db.Machines.SingleAsync();
        var ownerCompanyId = machine.CompanyId!.Value;
        var previousCompanyId = Guid.NewGuid();
        scenario.Db.Companies.Add(new Company
        {
            Id = previousCompanyId,
            Name = "Empresa anterior",
            Cnpj = "55555555000155"
        });
        scenario.Db.MachineConnectionStates.Add(new MachineConnectionState
        {
            MachineId = machine.Id,
            CompanyId = previousCompanyId,
            MonitoringEnabled = true,
            MdbStatus = MdbStatuses.Vend,
            MdbStatusRaw = "{\"status\":\"vend_state\"}",
            MdbStatusUpdatedAt = DateTime.UtcNow,
            AutoOpenLastError = "erro de outra empresa"
        });
        await scenario.Db.SaveChangesAsync();

        using var socket = new ScriptedWebSocket();
        socket.SendText(StartupFrame(scenario.Credential));
        var handshake = scenario.Manager.HandleDeviceAsync(socket, CancellationToken.None);
        await socket.FirstTextSent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        MachineConnectionState state;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        do
        {
            state = await scenario.Db.MachineConnectionStates.AsNoTracking().SingleAsync();
            if (state.CompanyId == ownerCompanyId && state.IsOnline) break;
            await Task.Delay(20);
        } while (DateTime.UtcNow < deadline);

        Assert.Equal(ownerCompanyId, state.CompanyId);
        Assert.True(state.IsOnline);
        Assert.True(state.OwnerLeaseExpiresAt > DateTime.UtcNow);
        Assert.False(state.MonitoringEnabled);
        Assert.Null(state.MdbStatus);
        Assert.Null(state.MdbStatusRaw);
        Assert.Null(state.MdbStatusUpdatedAt);
        Assert.Null(state.AutoOpenLastError);

        socket.SendClose();
        await handshake.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ValidCredentialCompletesStartHandshakeAndAcknowledgesMachine()
    {
        await using var scenario = await CreateScenarioAsync(requireCredentials: true, revokedAt: null);
        using var socket = new ScriptedWebSocket();
        socket.SendText(StartupFrame(scenario.Credential));
        var handshake = scenario.Manager.HandleDeviceAsync(socket, CancellationToken.None);
        await socket.FirstTextSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        socket.SendClose();

        await handshake;

        Assert.Contains(socket.SentMessages, message => message.Contains("\"type\":\"ack\"", StringComparison.Ordinal));
        Assert.Equal(WebSocketCloseStatus.NormalClosure, socket.CloseStatus);
        var state = await scenario.Db.MachineConnectionStates.SingleAsync();
        Assert.False(state.IsOnline);
        Assert.Null(state.ConnectionId);
        var connectionEvents = await scenario.Db.TelemetryEvents.Select(x => x.EventType).ToListAsync();
        Assert.Equal(2, connectionEvents.Count);
        Assert.Contains("device.connected", connectionEvents);
        Assert.Contains("device.disconnected", connectionEvents);
    }

    [Fact]
    public async Task ClientInitiatedCloseResponseHonorsRequestCancellation()
    {
        await using var scenario = await CreateScenarioAsync(requireCredentials: false, revokedAt: null);
        using var socket = new ScriptedWebSocket { BlockCloseOutput = true };
        using var requestCancellation = new CancellationTokenSource();
        socket.SendText(StartupFrame(credential: null));

        var handshake = scenario.Manager.HandleDeviceAsync(socket, requestCancellation.Token);
        await socket.FirstTextSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        socket.SendClose();
        await socket.CloseOutputStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        requestCancellation.Cancel();
        try
        {
            await socket.CloseOutputCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await handshake.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            socket.ReleaseCloseOutput();
            await handshake.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task RevokedCredentialIsRejectedBeforeMachineIsRegistered()
    {
        await using var scenario = await CreateScenarioAsync(requireCredentials: true, revokedAt: DateTime.UtcNow);
        using var socket = new ScriptedWebSocket();
        socket.SendText(StartupFrame(scenario.Credential));

        await scenario.Manager.HandleDeviceAsync(socket, CancellationToken.None);

        Assert.Equal(WebSocketCloseStatus.PolicyViolation, socket.CloseStatus);
        Assert.DoesNotContain(socket.SentMessages, message => message.Contains("\"type\":\"ack\"", StringComparison.Ordinal));
        Assert.Empty(await scenario.Db.MachineConnectionStates.ToListAsync());
    }

    [Fact]
    public async Task RejectedHandshakeAbortsWhenPeerDoesNotCompleteCloseHandshake()
    {
        await using var scenario = await CreateScenarioAsync(requireCredentials: true, revokedAt: DateTime.UtcNow);
        using var socket = new ScriptedWebSocket { BlockClose = true };
        socket.SendText(StartupFrame(scenario.Credential));

        var rejectedHandshake = scenario.Manager.HandleDeviceAsync(socket, CancellationToken.None);
        try
        {
            await rejectedHandshake.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            socket.ReleaseClose();
            await rejectedHandshake.WaitAsync(TimeSpan.FromSeconds(2));
        }

        Assert.True(socket.CloseCancellationObserved.Task.IsCompleted);
        Assert.True(socket.WasAborted);
        Assert.Empty(await scenario.Db.MachineConnectionStates.ToListAsync());
    }

    [Fact]
    public async Task CredentialRevocationDoesNotWaitForPeerCloseHandshake()
    {
        await using var scenario = await CreateScenarioAsync(requireCredentials: false, revokedAt: null);
        using var socket = new ScriptedWebSocket { BlockClose = true };
        socket.SendText(StartupFrame(credential: null));
        var handshake = scenario.Manager.HandleDeviceAsync(socket, CancellationToken.None);
        await socket.FirstTextSent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await scenario.Manager.DisconnectAsync("SERIAL-1", "Credencial revogada.").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(socket.CloseCancellationObserved.Task.IsCompleted);
        Assert.True(socket.WasAborted);
        Assert.False(scenario.Manager.IsOnline("SERIAL-1"));
        Assert.False((await scenario.Db.MachineConnectionStates.SingleAsync()).IsOnline);
        await handshake.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task OptionalCredentialModeAuthenticatesProvidedCredentialsBeforeFleetEnforcement()
    {
        await using var scenario = await CreateScenarioAsync(requireCredentials: false, revokedAt: null);

        using var invalidSocket = new ScriptedWebSocket();
        invalidSocket.SendText(StartupFrame("vmc_invalid"));
        await scenario.Manager.HandleDeviceAsync(invalidSocket, CancellationToken.None);

        Assert.Equal(WebSocketCloseStatus.PolicyViolation, invalidSocket.CloseStatus);
        Assert.Empty(await scenario.Db.MachineConnectionStates.ToListAsync());

        using var credentialedSocket = new ScriptedWebSocket();
        credentialedSocket.SendText(StartupFrame(scenario.Credential));
        var handshake = scenario.Manager.HandleDeviceAsync(credentialedSocket, CancellationToken.None);
        await credentialedSocket.FirstTextSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        credentialedSocket.SendClose();
        await handshake.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(credentialedSocket.SentMessages, message => message.Contains("\"type\":\"ack\"", StringComparison.Ordinal));
        Assert.Equal(WebSocketCloseStatus.NormalClosure, credentialedSocket.CloseStatus);
    }

    [Fact]
    public async Task LegacyDuplicateHandshakeIsRejectedAndLeavesExistingSocketOnline()
    {
        await using var scenario = await CreateScenarioAsync(requireCredentials: false, revokedAt: null);
        using var existingSocket = new ScriptedWebSocket();
        existingSocket.SendText(StartupFrame(credential: null));
        var existingHandshake = scenario.Manager.HandleDeviceAsync(existingSocket, CancellationToken.None);
        await existingSocket.FirstTextSent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var duplicateSocket = new ScriptedWebSocket { BlockClose = true };
        duplicateSocket.SendText(StartupFrame(credential: null));
        await scenario.Manager.HandleDeviceAsync(duplicateSocket, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, duplicateSocket.CloseStatus);
        Assert.True(duplicateSocket.CloseCancellationObserved.Task.IsCompleted);
        Assert.True(duplicateSocket.WasAborted);
        Assert.True(scenario.Manager.IsOnline("SERIAL-1"));
        Assert.False(existingSocket.WasAborted);
        var state = await scenario.Db.MachineConnectionStates.SingleAsync();
        Assert.True(state.IsOnline);
        Assert.Equal(scenario.Manager.NodeId, state.OwnerNodeId);
        Assert.Equal(state.ConnectionId, state.OwnerConnectionId);
        Assert.True(state.OwnerLeaseExpiresAt > DateTime.UtcNow);

        existingSocket.SendClose();
        await existingHandshake.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ShutdownRejectsNewCommandsAndClosesConnectedMachineForReconnect()
    {
        await using var scenario = await CreateScenarioAsync(requireCredentials: false, revokedAt: null);
        using var socket = new ScriptedWebSocket();
        socket.SendText(StartupFrame(credential: null));
        var handshake = scenario.Manager.HandleDeviceAsync(socket, CancellationToken.None);
        await socket.FirstTextSent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var shutdown = scenario.Manager.StopAsync(CancellationToken.None);
        var command = await scenario.Manager.SendCommandAsync("SERIAL-1", "MDB_STATUS");

        using var reconnectDuringShutdown = new ScriptedWebSocket();
        reconnectDuringShutdown.SendText(StartupFrame(credential: null));
        await scenario.Manager.HandleDeviceAsync(reconnectDuringShutdown, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        await handshake.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(command.Success);
        Assert.Equal("server_restarting", command.Error);
        Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, reconnectDuringShutdown.CloseStatus);
        Assert.Empty(reconnectDuringShutdown.SentMessages);
        Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, socket.CloseStatus);
        Assert.Contains("Reconecte", socket.CloseStatusDescription);
        Assert.False(scenario.Manager.IsOnline("SERIAL-1"));
        var state = await scenario.Db.MachineConnectionStates.SingleAsync();
        Assert.False(state.IsOnline);
        Assert.Null(state.ConnectionId);
        Assert.Null(state.OwnerNodeId);
        Assert.Null(state.OwnerConnectionId);
    }

    [Fact]
    public async Task ShutdownWaitsForInFlightMachineCommandAcknowledgementBeforeClosingSocket()
    {
        await using var scenario = await CreateScenarioAsync(requireCredentials: false, revokedAt: null);
        using var socket = new ScriptedWebSocket();
        socket.SendText(StartupFrame(credential: null));
        var handshake = scenario.Manager.HandleDeviceAsync(socket, CancellationToken.None);
        await socket.FirstTextSent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var command = scenario.Manager.SendCommandAsync("SERIAL-1", "MDB_STATUS");
        string? commandFrame = null;
        var commandDeadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < commandDeadline)
        {
            commandFrame = socket.SentMessages.FirstOrDefault(message =>
                message.Contains("\"data\":\"MDB_STATUS\"", StringComparison.Ordinal));
            if (commandFrame != null) break;
            await Task.Delay(25);
        }

        Assert.NotNull(commandFrame);
        var shutdown = scenario.Manager.StopAsync(CancellationToken.None);
        var restartProbe = await scenario.Manager.SendCommandAsync("SERIAL-1", "SHUTDOWN_PROBE")
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("server_restarting", restartProbe.Error);
        Assert.False(shutdown.IsCompleted);
        Assert.Equal(WebSocketState.Open, socket.State);

        using (var sentCommand = JsonDocument.Parse(commandFrame!))
        {
            var commandId = sentCommand.RootElement.GetProperty("id").GetInt32();
            socket.SendText($"{{\"type\":\"ack\",\"id\":{commandId}}}");
        }

        var commandResult = await command.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(commandResult.Success);
        await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        await handshake.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, socket.SentMessages.Count(message =>
            message.Contains("\"data\":\"MDB_STATUS\"", StringComparison.Ordinal)));
        Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, socket.CloseStatus);
        Assert.False(scenario.Manager.IsOnline("SERIAL-1"));
    }

    [Fact]
    public async Task CommandCancellationAfterEnqueueReturnsDurableUncertainOutcome()
    {
        await using var scenario = await CreateScenarioAsync(requireCredentials: false, revokedAt: null);
        using var socket = new ScriptedWebSocket();
        using var cancellation = new CancellationTokenSource();
        socket.SendText(StartupFrame(credential: null));
        var handshake = scenario.Manager.HandleDeviceAsync(socket, CancellationToken.None);
        await socket.FirstTextSent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var commandTask = scenario.Manager.SendCommandAsync("SERIAL-1", "TEST_COMMAND", cancellation.Token);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        string? commandFrame = null;
        while (DateTime.UtcNow < deadline)
        {
            commandFrame = socket.SentMessages.FirstOrDefault(message =>
                message.Contains("\"data\":\"TEST_COMMAND\"", StringComparison.Ordinal));
            if (commandFrame != null) break;
            await Task.Delay(20);
        }

        Assert.NotNull(commandFrame);
        cancellation.Cancel();
        var result = await commandTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.Success);
        Assert.Equal("command_outcome_unknown", result.Error);
        Assert.Equal(1, result.Attempts);
        Assert.Single(socket.SentMessages, message => message.Contains("\"data\":\"TEST_COMMAND\"", StringComparison.Ordinal));

        socket.SendClose();
        await handshake.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task FinalLocalCommandAckTimeoutReturnsExplicitFailure()
    {
        await using var scenario = await CreateScenarioAsync(requireCredentials: false, revokedAt: null);
        using var socket = new ScriptedWebSocket();
        socket.SendText(StartupFrame(credential: null));
        var handshake = scenario.Manager.HandleDeviceAsync(socket, CancellationToken.None);
        await socket.FirstTextSent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var result = await scenario.Manager.SendCommandAsync("SERIAL-1", "TEST_TIMEOUT").WaitAsync(TimeSpan.FromSeconds(20));

        Assert.False(result.Success);
        Assert.Equal("ack_timeout", result.Error);
        Assert.Equal(3, result.Attempts);
        var commandFrames = socket.SentMessages
            .Where(message => message.Contains("\"data\":\"TEST_TIMEOUT\"", StringComparison.Ordinal))
            .Select(message => JsonDocument.Parse(message))
            .ToArray();
        Assert.Equal(3, commandFrames.Length);
        Assert.Single(commandFrames.Select(document => document.RootElement.GetProperty("id").GetInt32()).Distinct());
        foreach (var document in commandFrames) document.Dispose();

        socket.SendClose();
        await handshake.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ApplicationStoppingDrainsMachineSocketBeforeHostStopsRequests()
    {
        var lifetime = new TestHostApplicationLifetime();
        await using var scenario = await CreateScenarioAsync(requireCredentials: false, revokedAt: null, lifetime);
        using var socket = new ScriptedWebSocket();
        socket.SendText(StartupFrame(credential: null));
        var handshake = scenario.Manager.HandleDeviceAsync(socket, CancellationToken.None);
        await socket.FirstTextSent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        lifetime.StopApplication();
        await handshake.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, socket.CloseStatus);
        Assert.Contains("Reconecte", socket.CloseStatusDescription);
        Assert.False(scenario.Manager.IsOnline("SERIAL-1"));
        var state = await scenario.Db.MachineConnectionStates.SingleAsync();
        Assert.False(state.IsOnline);
        Assert.Null(state.ConnectionId);
        Assert.Null(state.OwnerNodeId);
        Assert.Null(state.OwnerConnectionId);
        Assert.Equal("server_restarting", (await scenario.Manager.SendCommandAsync("SERIAL-1", "MDB_STATUS")).Error);
    }

    [Fact]
    public async Task ApplicationStoppingBoundsDrainWhenMachineDoesNotAcknowledgeCommand()
    {
        var lifetime = new TestHostApplicationLifetime();
        await using var scenario = await CreateScenarioAsync(
            requireCredentials: false,
            revokedAt: null,
            lifetime,
            applicationStoppingDrainTimeoutSeconds: 1);
        using var socket = new ScriptedWebSocket();
        socket.SendText(StartupFrame(credential: null));
        var handshake = scenario.Manager.HandleDeviceAsync(socket, CancellationToken.None);
        await socket.FirstTextSent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var commandCancellation = new CancellationTokenSource();
        var command = scenario.Manager.SendCommandAsync("SERIAL-1", "MDB_STATUS", commandCancellation.Token);
        string? commandFrame = null;
        var commandDeadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < commandDeadline)
        {
            commandFrame = socket.SentMessages.FirstOrDefault(message =>
                message.Contains("\"data\":\"MDB_STATUS\"", StringComparison.Ordinal));
            if (commandFrame != null) break;
            await Task.Delay(25);
        }

        Assert.NotNull(commandFrame);
        var stopwatch = Stopwatch.StartNew();
        lifetime.StopApplication();
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"A drenagem excedeu o limite de encerramento configurado: {stopwatch.Elapsed}.");
        await handshake.WaitAsync(TimeSpan.FromSeconds(5));
        commandCancellation.Cancel();
        var commandResult = await command.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("command_outcome_unknown", commandResult.Error);
        Assert.True(socket.WasAborted);
        Assert.False(scenario.Manager.IsOnline("SERIAL-1"));
    }

    [Fact]
    public async Task OldSocketClosingAfterReconnectDoesNotMarkNewOwnerOffline()
    {
        var connectionString = $"Data Source=telemetry-overlap-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var databaseKeeper = new SqliteConnection(connectionString);
        await databaseKeeper.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
        await using (var db = new AppDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            var company = new Company
            {
                Id = Guid.NewGuid(),
                Name = "Empresa de sobreposição",
                ActivationStatus = CompanyActivationStatuses.Active
            };
            db.Companies.Add(company);
            db.Machines.Add(new Machine
            {
                Id = "machine-overlap",
                Name = "Máquina de sobreposição",
                SerialNumber = "SERIAL-OVERLAP",
                NormalizedSerialNumber = "SERIAL-OVERLAP",
                CompanyId = company.Id
            });
            await db.SaveChangesAsync();
        }

        await using var oldNode = CreateNode(connectionString);
        await using var newNode = CreateNode(connectionString);
        await oldNode.Manager.StartAsync(CancellationToken.None);
        await newNode.Manager.StartAsync(CancellationToken.None);
        using var oldSocket = new ScriptedWebSocket();
        oldSocket.SendText("{\"id\":1,\"type\":\"start\",\"target\":\"SERIAL-OVERLAP\"}");
        var oldHandshake = oldNode.Manager.HandleDeviceAsync(oldSocket, CancellationToken.None);
        await oldSocket.FirstTextSent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var newSocket = new ScriptedWebSocket();
        newSocket.SendText("{\"id\":1,\"type\":\"start\",\"target\":\"SERIAL-OVERLAP\"}");
        var newHandshake = newNode.Manager.HandleDeviceAsync(newSocket, CancellationToken.None);
        await newSocket.FirstTextSent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        MachineConnectionState? currentOwner = null;
        var ownerDeadline = DateTime.UtcNow.AddSeconds(5);
        do
        {
            await using var db = new AppDbContext(options);
            currentOwner = await db.MachineConnectionStates.AsNoTracking()
                .SingleOrDefaultAsync(state => state.MachineId == "machine-overlap");
            if (currentOwner?.OwnerNodeId == newNode.Manager.NodeId) break;
            await Task.Delay(25);
        } while (DateTime.UtcNow < ownerDeadline);

        Assert.NotNull(currentOwner);
        Assert.Equal(newNode.Manager.NodeId, currentOwner.OwnerNodeId);
        Assert.Equal(TelemetryConnectionManager.CurrentCommandRoutingVersion, currentOwner.CommandRoutingVersion);
        var newOwnerConnectionId = currentOwner.ConnectionId;
        Assert.False(string.IsNullOrWhiteSpace(newOwnerConnectionId));

        oldSocket.SendClose();
        await oldHandshake.WaitAsync(TimeSpan.FromSeconds(5));

        await using (var db = new AppDbContext(options))
        {
            var state = await db.MachineConnectionStates.AsNoTracking()
                .SingleAsync(item => item.MachineId == "machine-overlap");
            Assert.True(state.IsOnline);
            Assert.Equal(newNode.Manager.NodeId, state.OwnerNodeId);
            Assert.Equal(newOwnerConnectionId, state.ConnectionId);
            Assert.Equal(TelemetryConnectionManager.CurrentCommandRoutingVersion, state.CommandRoutingVersion);
        }
        Assert.True(newNode.Manager.IsOnline("SERIAL-OVERLAP"));

        var commandFromOldNode = oldNode.Manager.SendCommandAsync("SERIAL-OVERLAP", "MDB_STATUS");
        string? commandFrame = null;
        var commandDeadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < commandDeadline)
        {
            commandFrame = newSocket.SentMessages.FirstOrDefault(message =>
                message.Contains("\"data\":\"MDB_STATUS\"", StringComparison.Ordinal));
            if (commandFrame != null) break;
            await Task.Delay(25);
        }

        Assert.NotNull(commandFrame);
        using (var command = JsonDocument.Parse(commandFrame!))
            newSocket.SendText($"{{\"type\":\"ack\",\"id\":{command.RootElement.GetProperty("id").GetInt32()}}}");
        Assert.True((await commandFromOldNode.WaitAsync(TimeSpan.FromSeconds(5))).Success);
        Assert.Equal(1, newSocket.SentMessages.Count(message =>
            message.Contains("\"data\":\"MDB_STATUS\"", StringComparison.Ordinal)));

        newSocket.SendClose();
        await newHandshake.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RealWebSocketAcknowledgesDuplicatesAndPersistsDifferentPayloadForSameId()
    {
        var connectionString = $"Data Source=telemetry-frame-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var keeper = new SqliteConnection(connectionString);
        await keeper.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
        var companyId = Guid.NewGuid();
        const string serial = "SERIAL-FRAME-TEST";
        await using (var setup = new AppDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.Companies.Add(new Company { Id = companyId, Name = "Empresa sintética", ActivationStatus = CompanyActivationStatuses.Active });
            setup.Machines.Add(new Machine { Id = "frame-machine", Name = "Máquina sintética", SerialNumber = serial, NormalizedSerialNumber = serial, CompanyId = companyId });
            await setup.SaveChangesAsync();
        }
        await using var server = await RunningTelemetryServer.StartAsync(connectionString, deviceTelemetryMessageHandler: new TestDeviceTelemetryMessageHandler());
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(server.WebSocketUri, CancellationToken.None);
        await SendStartFrameAsync(socket, serial);
        Assert.Contains("\"type\":\"ack\"", await ReceiveWebSocketTextAsync(socket, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        var firstData = new { command = "pool", type = "begin_session" };
        var secondData = new { command = "pool", type = "end_session" };
        foreach (var data in new[] { firstData, firstData, secondData })
        {
            await SendDeviceMessageAsync(socket, serial, 42, data);
            using var ack = JsonDocument.Parse(await ReceiveWebSocketTextAsync(socket, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("ack", ack.RootElement.GetProperty("type").GetString());
            Assert.Equal(42, ack.RootElement.GetProperty("id").GetInt32());
        }
        await using (var verify = new AppDbContext(options))
        {
            var messages = await verify.DeviceMessageInbox.AsNoTracking().OrderBy(item => item.ReceivedAt).ToListAsync();
            Assert.Equal(2, messages.Count);
            Assert.Contains("begin_session", messages[0].DataJson);
            Assert.Contains("end_session", messages[1].DataJson);
        }
        var malformed = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "msg", target = serial, data = firstData }));
        await socket.SendAsync(malformed, WebSocketMessageType.Text, true, CancellationToken.None);
        var buffer = new byte[256];
        await Assert.ThrowsAsync<WebSocketException>(async () =>
            await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        await server.RequestCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using var finalVerification = new AppDbContext(options);
        Assert.Equal(2, await finalVerification.DeviceMessageInbox.CountAsync());
    }

    [Fact]
    public async Task RealWebSocketDoesNotAcknowledgeMessageWhenInboxWriteFailsAndAcceptsRetry()
    {
        var connectionString = $"Data Source=telemetry-inbox-failure-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var keeper = new SqliteConnection(connectionString);
        await keeper.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
        var companyId = Guid.NewGuid();
        const string serial = "SERIAL-INBOX-FAILURE";
        await using (var setup = new AppDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.Companies.Add(new Company { Id = companyId, Name = "Empresa sintética", ActivationStatus = CompanyActivationStatuses.Active });
            setup.Machines.Add(new Machine { Id = "failure-machine", Name = "Máquina sintética", SerialNumber = serial, NormalizedSerialNumber = serial, CompanyId = companyId });
            await setup.SaveChangesAsync();
            await setup.Database.ExecuteSqlRawAsync("CREATE TRIGGER inbox_test_failure BEFORE INSERT ON DeviceMessageInbox BEGIN SELECT RAISE(ABORT, 'synthetic inbox failure'); END;");
        }
        await using var server = await RunningTelemetryServer.StartAsync(connectionString, deviceTelemetryMessageHandler: new TestDeviceTelemetryMessageHandler());
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(server.WebSocketUri, CancellationToken.None);
        await SendStartFrameAsync(socket, serial);
        Assert.Contains("\"type\":\"ack\"", await ReceiveWebSocketTextAsync(socket, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        var data = new { command = "pool", type = "begin_session" };
        await SendDeviceMessageAsync(socket, serial, 77, data);
        var buffer = new byte[256];
        await Assert.ThrowsAsync<WebSocketException>(async () =>
            await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        await server.RequestCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using (var verify = new AppDbContext(options))
        {
            Assert.Equal(0, await verify.DeviceMessageInbox.CountAsync());
            await verify.Database.ExecuteSqlRawAsync("DROP TRIGGER inbox_test_failure;");
        }
        using var retrySocket = new ClientWebSocket();
        await retrySocket.ConnectAsync(server.WebSocketUri, CancellationToken.None);
        await SendStartFrameAsync(retrySocket, serial);
        Assert.Contains("\"type\":\"ack\"", await ReceiveWebSocketTextAsync(retrySocket, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        await SendDeviceMessageAsync(retrySocket, serial, 77, data);
        using var ack = JsonDocument.Parse(await ReceiveWebSocketTextAsync(retrySocket, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("ack", ack.RootElement.GetProperty("type").GetString());
        Assert.Equal(77, ack.RootElement.GetProperty("id").GetInt32());
        await using var finalVerification = new AppDbContext(options);
        Assert.Equal(1, await finalVerification.DeviceMessageInbox.CountAsync());
        await finalVerification.Database.ExecuteSqlRawAsync("CREATE TRIGGER inbox_test_failure BEFORE INSERT ON DeviceMessageInbox BEGIN SELECT RAISE(ABORT, 'synthetic inbox failure'); END;");
        var statusRequest = server.Manager.SendStatusRequestAsync(serial, TimeSpan.FromSeconds(5));
        using var statusCommand = JsonDocument.Parse(await ReceiveWebSocketTextAsync(retrySocket, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        var statusId = statusCommand.RootElement.GetProperty("id").GetInt32();
        Assert.Equal("MDB_STATUS", statusCommand.RootElement.GetProperty("data").GetString());
        await SendDeviceMessageAsync(retrySocket, serial, statusId, new { command = "status", status = "idle_state" });
        var result = await statusRequest.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.False(result.Success);
        Assert.Null(result.Status);
        await Assert.ThrowsAsync<WebSocketException>(async () =>
            await retrySocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, await finalVerification.DeviceMessageInbox.CountAsync());
    }

    [Fact]
    public async Task RealWebSocketReplayAfterReconnectDoesNotDuplicateActiveSessionMessage()
    {
        var connectionString = $"Data Source=telemetry-replay-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var keeper = new SqliteConnection(connectionString);
        await keeper.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
        var companyId = Guid.NewGuid();
        const string serial = "SERIAL-REPLAY";
        const string machineId = "replay-machine";
        await using (var setup = new AppDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.Companies.Add(new Company { Id = companyId, Name = "Empresa sintética", ActivationStatus = CompanyActivationStatuses.Active });
            setup.Machines.Add(new Machine { Id = machineId, Name = "Máquina sintética", SerialNumber = serial, NormalizedSerialNumber = serial, CompanyId = companyId });
            setup.MachineSessions.Add(new MachineSession { MachineId = machineId, CompanyId = companyId, State = MachineSessionStates.AwaitingSelection });
            await setup.SaveChangesAsync();
        }
        await using var server = await RunningTelemetryServer.StartAsync(connectionString, deviceTelemetryMessageHandler: new TestDeviceTelemetryMessageHandler());
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(server.WebSocketUri, CancellationToken.None);
        await SendStartFrameAsync(socket, serial);
        Assert.Contains("\"type\":\"ack\"", await ReceiveWebSocketTextAsync(socket, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        var data = new { command = "vend", type = "success" };
        await SendDeviceMessageAsync(socket, serial, 42, data);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        var persisted = false;
        while (DateTime.UtcNow < deadline)
        {
            await using var db = new AppDbContext(options);
            persisted = await db.DeviceMessageInbox.AsNoTracking().AnyAsync();
            if (persisted) break;
            await Task.Delay(25);
        }
        Assert.True(persisted);
        socket.Abort(); // Discard any ACK that may already have reached the client.
        await server.RequestCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var retry = new ClientWebSocket();
        await retry.ConnectAsync(server.WebSocketUri, CancellationToken.None);
        await SendStartFrameAsync(retry, serial);
        Assert.Contains("\"type\":\"ack\"", await ReceiveWebSocketTextAsync(retry, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        await SendDeviceMessageAsync(retry, serial, 42, data);
        using var ack = JsonDocument.Parse(await ReceiveWebSocketTextAsync(retry, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("ack", ack.RootElement.GetProperty("type").GetString());
        Assert.Equal(42, ack.RootElement.GetProperty("id").GetInt32());
        await using var verify = new AppDbContext(options);
        Assert.Equal(1, await verify.DeviceMessageInbox.CountAsync());
        await retry.CloseAsync(WebSocketCloseStatus.NormalClosure, "test complete", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RealWebSocketReconnectsToNewInstanceAfterGracefulShutdown()
    {
        var connectionString = $"Data Source=telemetry-kestrel-reconnect-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var databaseKeeper = new SqliteConnection(connectionString);
        await databaseKeeper.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
        Guid companyId;
        await using (var db = new AppDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            companyId = Guid.NewGuid();
            var company = new Company
            {
                Id = companyId,
                Name = "Empresa WebSocket loopback",
                ActivationStatus = CompanyActivationStatuses.Active
            };
            db.Companies.Add(company);
            db.Machines.Add(new Machine
            {
                Id = "machine-kestrel-reconnect",
                Name = "Máquina WebSocket loopback",
                SerialNumber = "SERIAL-KESTREL-RECONNECT",
                NormalizedSerialNumber = "SERIAL-KESTREL-RECONNECT",
                CompanyId = companyId
            });
            await db.SaveChangesAsync();
        }

        await using var firstInstance = await RunningTelemetryServer.StartAsync(connectionString);
        using var machineSocket = new ClientWebSocket();
        await machineSocket.ConnectAsync(firstInstance.WebSocketUri, CancellationToken.None);
        await SendStartFrameAsync(machineSocket);
        Assert.Contains("\"type\":\"ack\"", await ReceiveWebSocketTextAsync(machineSocket, CancellationToken.None));

        var oldOwner = firstInstance.Manager.NodeId;
        var firstShutdown = firstInstance.Manager.StopAsync(CancellationToken.None);
        var closeBuffer = new byte[256];
        var closeFrame = await machineSocket.ReceiveAsync(new ArraySegment<byte>(closeBuffer), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WebSocketMessageType.Close, closeFrame.MessageType);
        Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, closeFrame.CloseStatus);
        await machineSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "reconnect accepted", CancellationToken.None);
        await firstShutdown.WaitAsync(TimeSpan.FromSeconds(5));
        await firstInstance.RequestCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await using var newInstance = await RunningTelemetryServer.StartAsync(connectionString);
        using var reconnectedMachineSocket = new ClientWebSocket();
        await reconnectedMachineSocket.ConnectAsync(newInstance.WebSocketUri, CancellationToken.None);
        await SendStartFrameAsync(reconnectedMachineSocket);
        Assert.Contains("\"type\":\"ack\"", await ReceiveWebSocketTextAsync(reconnectedMachineSocket, CancellationToken.None));

        MachineConnectionState? state = null;
        var ownerDeadline = DateTime.UtcNow.AddSeconds(5);
        do
        {
            await using var db = new AppDbContext(options);
            state = await db.MachineConnectionStates.AsNoTracking()
                .SingleOrDefaultAsync(item => item.MachineId == "machine-kestrel-reconnect");
            if (state?.OwnerNodeId == newInstance.Manager.NodeId) break;
            await Task.Delay(25);
        } while (DateTime.UtcNow < ownerDeadline);

        Assert.NotNull(state);
        Assert.True(state.IsOnline);
        Assert.Equal(companyId, state.CompanyId);
        Assert.Equal(newInstance.Manager.NodeId, state.OwnerNodeId);
        Assert.NotEqual(oldOwner, state.OwnerNodeId);

        var secondShutdown = newInstance.Manager.StopAsync(CancellationToken.None);
        var finalClose = await reconnectedMachineSocket.ReceiveAsync(new ArraySegment<byte>(closeBuffer), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, finalClose.CloseStatus);
        await reconnectedMachineSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test complete", CancellationToken.None);
        await secondShutdown.WaitAsync(TimeSpan.FromSeconds(5));
        await newInstance.RequestCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task FleetOfSeventyEightRealWebSocketsReconnectsAfterGracefulShutdown()
    {
        const int machineCount = 78;
        var connectionString = $"Data Source=telemetry-kestrel-fleet-reconnect-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var databaseKeeper = new SqliteConnection(connectionString);
        await databaseKeeper.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
        var companyId = Guid.NewGuid();
        var machineIds = Enumerable.Range(1, machineCount).Select(index => $"machine-fleet-{index:D3}").ToArray();
        var serials = Enumerable.Range(1, machineCount).Select(index => $"SERIAL-FLEET-{index:D3}").ToArray();

        await using (var db = new AppDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.Companies.Add(new Company
            {
                Id = companyId,
                Name = "Empresa sintética de reconexão em frota",
                ActivationStatus = CompanyActivationStatuses.Active
            });
            db.Machines.AddRange(Enumerable.Range(0, machineCount).Select(index => new Machine
            {
                Id = machineIds[index],
                Name = $"Máquina sintética {index + 1:D3}",
                SerialNumber = serials[index],
                NormalizedSerialNumber = serials[index],
                CompanyId = companyId
            }));
            await db.SaveChangesAsync();
        }

        var sockets = new List<ClientWebSocket>(machineCount);
        try
        {
            await using var firstInstance = await RunningTelemetryServer.StartAsync(connectionString);
            var oldNodeId = firstInstance.Manager.NodeId;
            foreach (var serial in serials)
            {
                var socket = new ClientWebSocket();
                sockets.Add(socket);
                await socket.ConnectAsync(firstInstance.WebSocketUri, CancellationToken.None);
                await SendStartFrameAsync(socket, serial);
                Assert.Contains("\"type\":\"ack\"", await ReceiveWebSocketTextAsync(socket, CancellationToken.None));
            }

            await WaitForMachineConnectionStateCountAsync(options, machineCount, online: true, TimeSpan.FromSeconds(10));
            await using (var db = new AppDbContext(options))
            {
                Assert.Equal(machineCount, await db.MachineConnectionStates.CountAsync(state => state.IsOnline));
                Assert.All(await db.MachineConnectionStates.AsNoTracking().ToListAsync(), state =>
                {
                    Assert.Equal(oldNodeId, state.OwnerNodeId);
                    Assert.Equal(companyId, state.CompanyId);
                });
            }

            var firstShutdown = firstInstance.Manager.StopAsync(CancellationToken.None);
            await Task.WhenAll(sockets.Select(async socket =>
            {
                var closeBuffer = new byte[256];
                var closeFrame = await socket.ReceiveAsync(new ArraySegment<byte>(closeBuffer), CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(WebSocketMessageType.Close, closeFrame.MessageType);
                Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, closeFrame.CloseStatus);
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "fleet reconnect accepted", CancellationToken.None);
            }));
            await firstShutdown.WaitAsync(TimeSpan.FromSeconds(15));
            try
            {
                await WaitForMachineConnectionStateCountAsync(options, machineCount, online: false, TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException exception)
            {
                var persistenceLogs = firstInstance.ManagerLogEntries
                    .Where(entry => entry.Contains("desconexão", StringComparison.OrdinalIgnoreCase))
                    .TakeLast(30);
                throw new TimeoutException(
                    $"{exception.Message} Logs de persistência: {string.Join(" | ", persistenceLogs)}",
                    exception);
            }
            await firstInstance.StopHostAsync();
            foreach (var socket in sockets) socket.Dispose();
            sockets.Clear();

            await using var newInstance = await RunningTelemetryServer.StartAsync(connectionString);
            foreach (var serial in serials)
            {
                var socket = new ClientWebSocket();
                sockets.Add(socket);
                await socket.ConnectAsync(newInstance.WebSocketUri, CancellationToken.None);
                await SendStartFrameAsync(socket, serial);
                Assert.Contains("\"type\":\"ack\"", await ReceiveWebSocketTextAsync(socket, CancellationToken.None));
            }

            await WaitForMachineConnectionStateCountAsync(options, machineCount, online: true, TimeSpan.FromSeconds(10));
            await using (var db = new AppDbContext(options))
            {
                var states = await db.MachineConnectionStates.AsNoTracking().ToListAsync();
                Assert.Equal(machineCount, states.Count);
                Assert.All(states, state =>
                {
                    Assert.True(state.IsOnline);
                    Assert.Equal(companyId, state.CompanyId);
                    Assert.Equal(newInstance.Manager.NodeId, state.OwnerNodeId);
                    Assert.NotEqual(oldNodeId, state.OwnerNodeId);
                });
            }

            var secondShutdown = newInstance.Manager.StopAsync(CancellationToken.None);
            await Task.WhenAll(sockets.Select(async socket =>
            {
                var closeBuffer = new byte[256];
                var closeFrame = await socket.ReceiveAsync(new ArraySegment<byte>(closeBuffer), CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(WebSocketMessageType.Close, closeFrame.MessageType);
                Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, closeFrame.CloseStatus);
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "fleet test complete", CancellationToken.None);
            }));
            await secondShutdown.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            foreach (var socket in sockets) socket.Dispose();
        }
    }

    [Fact]
    public async Task FleetPresenceIsPersistedAfterGracefulShutdownWithPostgres()
    {
        var configuredConnection = Environment.GetEnvironmentVariable("VENDMACHINES_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(configuredConnection))
        {
            if (string.Equals(
                    Environment.GetEnvironmentVariable("VENDMACHINES_REQUIRE_TEST_POSTGRES"),
                    "true",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("O teste de drenagem da frota exige PostgreSQL quando VENDMACHINES_REQUIRE_TEST_POSTGRES=true.");
            return;
        }

        const int machineCount = 78;
        var databaseName = $"vendmachines_telemetry_fleet_drain_{Guid.NewGuid():N}";
        var adminConnection = new NpgsqlConnectionStringBuilder(configuredConnection) { Database = "postgres" };
        var databaseConnection = new NpgsqlConnectionStringBuilder(configuredConnection) { Database = databaseName };
        await using (var admin = new NpgsqlConnection(adminConnection.ConnectionString))
        {
            await admin.OpenAsync();
            await using var createDatabase = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin);
            await createDatabase.ExecuteNonQueryAsync();
        }

        try
        {
            var connectionString = databaseConnection.ConnectionString;
            var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options;
            var companyId = Guid.NewGuid();
            var serials = Enumerable.Range(1, machineCount).Select(index => $"SERIAL-PG-FLEET-{index:D3}").ToArray();
            await using (var db = new AppDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                db.Companies.Add(new Company
                {
                    Id = companyId,
                    Name = "Empresa sintética PostgreSQL para drenagem de frota",
                    ActivationStatus = CompanyActivationStatuses.Active
                });
                db.Machines.AddRange(Enumerable.Range(1, machineCount).Select(index => new Machine
                {
                    Id = $"machine-pg-fleet-{index:D3}",
                    Name = $"Máquina PostgreSQL {index:D3}",
                    SerialNumber = serials[index - 1],
                    NormalizedSerialNumber = serials[index - 1],
                    CompanyId = companyId
                }));
                await db.SaveChangesAsync();
            }

            var sockets = new List<ClientWebSocket>(machineCount);
            try
            {
                await using var instance = await RunningTelemetryServer.StartAsync(connectionString, usePostgres: true);
                foreach (var serial in serials)
                {
                    var socket = new ClientWebSocket();
                    sockets.Add(socket);
                    await socket.ConnectAsync(instance.WebSocketUri, CancellationToken.None);
                    await SendStartFrameAsync(socket, serial);
                    Assert.Contains("\"type\":\"ack\"", await ReceiveWebSocketTextAsync(socket, CancellationToken.None));
                }

                await WaitForMachineConnectionStateCountAsync(options, machineCount, online: true, TimeSpan.FromSeconds(30));
                var shutdown = instance.Manager.StopAsync(CancellationToken.None);
                await Task.WhenAll(sockets.Select(async socket =>
                {
                    var closeBuffer = new byte[256];
                    var closeFrame = await socket.ReceiveAsync(new ArraySegment<byte>(closeBuffer), CancellationToken.None)
                        .WaitAsync(TimeSpan.FromSeconds(15));
                    Assert.Equal(WebSocketMessageType.Close, closeFrame.MessageType);
                    Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, closeFrame.CloseStatus);
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "drenagem PostgreSQL concluída", CancellationToken.None);
                }));
                await shutdown.WaitAsync(TimeSpan.FromSeconds(20));
                await WaitForMachineConnectionStateCountAsync(options, machineCount, online: false, TimeSpan.FromSeconds(15));
                await instance.StopHostAsync();
                foreach (var socket in sockets) socket.Dispose();
                sockets.Clear();

                await using var replacement = await RunningTelemetryServer.StartAsync(connectionString, usePostgres: true);
                foreach (var serial in serials)
                {
                    var socket = new ClientWebSocket();
                    sockets.Add(socket);
                    await socket.ConnectAsync(replacement.WebSocketUri, CancellationToken.None);
                    await SendStartFrameAsync(socket, serial);
                    Assert.Contains("\"type\":\"ack\"", await ReceiveWebSocketTextAsync(socket, CancellationToken.None));
                }
                await WaitForMachineConnectionStateCountAsync(options, machineCount, online: true, TimeSpan.FromSeconds(30));
                await using (var verification = new AppDbContext(options))
                {
                    var states = await verification.MachineConnectionStates.AsNoTracking().ToListAsync();
                    Assert.Equal(machineCount, states.Count);
                    Assert.All(states, state =>
                    {
                        Assert.True(state.IsOnline);
                        Assert.Equal(companyId, state.CompanyId);
                        Assert.Equal(replacement.Manager.NodeId, state.OwnerNodeId);
                        Assert.NotEqual(instance.Manager.NodeId, state.OwnerNodeId);
                    });
                }
                var replacementShutdown = replacement.Manager.StopAsync(CancellationToken.None);
                await Task.WhenAll(sockets.Select(async socket =>
                {
                    var closeFrame = await socket.ReceiveAsync(new ArraySegment<byte>(new byte[256]), CancellationToken.None)
                        .WaitAsync(TimeSpan.FromSeconds(15));
                    Assert.Equal(WebSocketMessageType.Close, closeFrame.MessageType);
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "teste de frota concluído", CancellationToken.None);
                }));
                await replacementShutdown.WaitAsync(TimeSpan.FromSeconds(20));
            }
            finally
            {
                foreach (var socket in sockets) socket.Dispose();
            }
        }
        finally
        {
            await using var admin = new NpgsqlConnection(adminConnection.ConnectionString);
            await admin.OpenAsync();
            await using var dropDatabase = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin);
            await dropDatabase.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task PaymentWebhookAndMdbDeliveryHandleSuccessRejectionAndOfflineMachineEndToEnd()
    {
        var connectionString = $"Data Source=payment-mdb-integration-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var databaseKeeper = new SqliteConnection(connectionString);
        await databaseKeeper.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
        var companyId = Guid.NewGuid();
        const string machineId = "machine-payment-mdb-integration";
        const string serial = "SERIAL-PAYMENT-MDB-INTEGRATION";
        const string providerPaymentId = "123456789";
        const string offlineMachineId = "machine-payment-mdb-offline";
        const string offlineSerial = "SERIAL-PAYMENT-MDB-OFFLINE";
        const string rejectedProviderPaymentId = "123456790";
        const string retryProviderPaymentId = "123456792";
        const string offlineProviderPaymentId = "123456791";
        var transactionId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var rejectedTransactionId = Guid.NewGuid();
        var rejectedSessionId = Guid.NewGuid();
        var retryTransactionId = Guid.NewGuid();
        var retrySessionId = Guid.NewGuid();
        var offlineTransactionId = Guid.NewGuid();
        var offlineSessionId = Guid.NewGuid();

        await using (var db = new AppDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.Companies.Add(new Company
            {
                Id = companyId,
                Name = "Empresa sintética de pagamento integrado",
                ActivationStatus = CompanyActivationStatuses.Active
            });
            db.Machines.AddRange(new Machine
            {
                Id = machineId,
                Name = "Máquina sintética de pagamento",
                SerialNumber = serial,
                NormalizedSerialNumber = serial,
                CompanyId = companyId
            }, new Machine
            {
                Id = offlineMachineId,
                Name = "Máquina sintética desconectada",
                SerialNumber = offlineSerial,
                NormalizedSerialNumber = offlineSerial,
                CompanyId = companyId,
            });
            db.PaymentTransactions.AddRange(new PaymentTransaction
            {
                Id = transactionId,
                CompanyId = companyId,
                MachineId = machineId,
                Amount = 5m,
                ApplicationFee = 0.25m,
                Status = "Pending",
                MercadoPagoPaymentId = providerPaymentId,
                SendTelemetryToMachine = true
            }, new PaymentTransaction
            {
                Id = rejectedTransactionId,
                CompanyId = companyId,
                MachineId = offlineMachineId,
                Amount = 7m,
                ApplicationFee = 0.35m,
                Status = "Pending",
                MercadoPagoPaymentId = rejectedProviderPaymentId,
                SendTelemetryToMachine = true
            });
            db.MachineSessions.AddRange(new MachineSession
            {
                Id = sessionId,
                CompanyId = companyId,
                MachineId = machineId,
                TransactionId = transactionId,
                State = MachineSessionStates.PaymentPending,
                ItemNumber = 1,
                AmountCents = 500,
                PaymentDeadlineAt = DateTime.UtcNow.AddMinutes(5)
            }, new MachineSession
            {
                Id = rejectedSessionId,
                CompanyId = companyId,
                MachineId = offlineMachineId,
                TransactionId = rejectedTransactionId,
                State = MachineSessionStates.PaymentPending,
                ItemNumber = 1,
                AmountCents = 700,
                PaymentDeadlineAt = DateTime.UtcNow.AddMinutes(5)
            });
            db.MachineProductSlots.Add(new MachineProductSlot
            {
                CompanyId = companyId,
                MachineId = machineId,
                ItemNumber = 1,
                ProductId = "product-water",
                Capacity = 10,
                CurrentStock = 3,
                LowStockThreshold = 1
            });
            db.Products.Add(new FullProduct
            {
                Id = "product-water",
                Code = "WATER-500",
                Name = "Água 500 ml",
                TypeId = "water",
                CompanyId = companyId
            });
            db.ProductTypes.Add(new ProductType { Id = "water", Name = "Água", CompanyId = companyId });
            db.MercadoPagoIntegrations.Add(new MercadoPagoIntegration
            {
                CompanyId = companyId,
                IsActive = true,
                AccessToken = EncryptionService.Encrypt("synthetic-access-token"),
                RefreshToken = EncryptionService.Encrypt("synthetic-refresh-token"),
                ClientSecret = EncryptionService.Encrypt("synthetic-client-secret"),
                ClientId = "synthetic-client",
                AccessTokenExpiresAt = DateTime.UtcNow.AddHours(1)
            });
            await db.SaveChangesAsync();
        }

        SessionOrchestrator? orchestrator = null;
        var handler = new DelegateDeviceTelemetryMessageHandler(
            (message, cancellationToken) => orchestrator!.HandleDeviceMessageAsync(message, cancellationToken));
        await using var server = await RunningTelemetryServer.StartAsync(
            connectionString,
            deviceTelemetryMessageHandler: handler);
        using var machineSocket = new ClientWebSocket();
        await machineSocket.ConnectAsync(server.WebSocketUri, CancellationToken.None);
        await SendStartFrameAsync(machineSocket, serial);
        Assert.Contains("\"type\":\"ack\"", await ReceiveWebSocketTextAsync(machineSocket, CancellationToken.None));
        using var offlineMachineSocket = new ClientWebSocket();
        await offlineMachineSocket.ConnectAsync(server.WebSocketUri, CancellationToken.None);
        await SendStartFrameAsync(offlineMachineSocket, offlineSerial);
        Assert.Contains("\"type\":\"ack\"", await ReceiveWebSocketTextAsync(offlineMachineSocket, CancellationToken.None));

        await using var processingDb = new AppDbContext(options);
        var panelHub = new TelemetryPanelHub(NullLogger<TelemetryPanelHub>.Instance);
        var providerHttpClientFactory = new FakeProviderHttpClientFactory();
        orchestrator = new SessionOrchestrator(
            processingDb,
            server.Manager,
            panelHub,
            providerHttpClientFactory,
            new ConfigurationBuilder().Build(),
            new TestWebHostEnvironment(),
            NullLogger<SessionOrchestrator>.Instance);

        var configuration = new ConfigurationBuilder().Build();
        var controller = new VendingMachines.Api.Controllers.PaymentsController(
            processingDb,
            orchestrator,
            server.ServiceProvider.GetRequiredService<IServiceScopeFactory>(),
            configuration,
            new TestWebHostEnvironment());
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        controller.HttpContext.Request.Headers["x-request-id"] = "synthetic-payment-mdb-flow";
        using var webhookDocument = JsonDocument.Parse(
            "{\"type\":\"payment\",\"action\":\"payment.updated\",\"data\":{\"id\":\"123456789\"}}");
        var webhookResult = Assert.IsType<OkObjectResult>(await controller.WebhookMercadoPago(webhookDocument.RootElement.Clone()));
        Assert.Contains("accepted", JsonSerializer.Serialize(webhookResult.Value));
        Assert.Equal("Pending", await processingDb.WebhookInboxMessages.Select(message => message.Status).SingleAsync());

        var processor = new MercadoPagoWebhookProcessor(
            processingDb,
            orchestrator,
            configuration,
            providerHttpClientFactory,
            NullLogger<MercadoPagoWebhookProcessor>.Instance);
        var processingTask = processor.ProcessNextAsync(CancellationToken.None);
        using var approvalCommand = JsonDocument.Parse(
            await ReceiveWebSocketTextAsync(machineSocket, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("msg", approvalCommand.RootElement.GetProperty("type").GetString());
        Assert.Equal("VENDA_APROVADA", approvalCommand.RootElement.GetProperty("data").GetString());
        await SendDeviceAckAsync(machineSocket, serial, approvalCommand.RootElement.GetProperty("id").GetInt32());
        Assert.True(await processingTask.WaitAsync(TimeSpan.FromSeconds(5)));

        await using (var verifyDb = new AppDbContext(options))
        {
            var session = await verifyDb.MachineSessions.AsNoTracking().SingleAsync(item => item.Id == sessionId);
            var transaction = await verifyDb.PaymentTransactions.AsNoTracking().SingleAsync(item => item.Id == transactionId);
            Assert.Equal(MachineSessionStates.PaymentApproved, session.State);
            Assert.Equal("Approved", transaction.Status);
            Assert.Single(await verifyDb.TelemetryCommands.Where(command => command.SessionId == sessionId).ToListAsync());
            Assert.Equal("Processed", await verifyDb.WebhookInboxMessages.Select(message => message.Status).SingleAsync());
        }

        await SendDeviceMessageAsync(machineSocket, serial, 2, new
        {
            command = "pool",
            type = "vend_approved",
            transactionId
        });
        await ProcessNextDeviceMessageAsync(server, machineSocket, serial);
        await SendDeviceMessageAsync(machineSocket, serial, 3, new
        {
            command = "vend",
            type = "success",
            transactionId
        });
        await using (var failureSetup = new AppDbContext(options))
            await failureSetup.Database.ExecuteSqlRawAsync("CREATE TRIGGER inbox_completion_failure BEFORE UPDATE ON DeviceMessageInbox WHEN NEW.Status = 'Processed' AND NEW.MessageId = 3 BEGIN SELECT RAISE(ABORT, 'synthetic completion failure'); END;");
        await ProcessNextDeviceMessageAsync(server, machineSocket, serial);

        await using (var verifyDb = new AppDbContext(options))
        {
            var session = await verifyDb.MachineSessions.AsNoTracking().SingleAsync(item => item.Id == sessionId);
            var sale = await verifyDb.SaleRecords.AsNoTracking().SingleAsync(item => item.TransactionId == transactionId);
            var slot = await verifyDb.MachineProductSlots.AsNoTracking().SingleAsync(item => item.MachineId == machineId);
            var movements = await verifyDb.InventoryMovements.AsNoTracking()
                .Where(item => item.MachineId == machineId).ToListAsync();

            Assert.Equal(MachineSessionStates.Completed, session.State);
            Assert.Equal("Settled", sale.FinancialStatus);
            Assert.Equal("product-water", sale.ProductId);
            Assert.Equal("Água 500 ml", sale.ProductNameSnapshot);
            Assert.Equal(5m, sale.GrossAmount);
            Assert.Equal(0.25m, sale.ApplicationFee);
            Assert.Equal(4.75m, sale.NetAmount);
            Assert.Equal(2, slot.CurrentStock);
            Assert.Single(movements);
            Assert.Equal(-1, movements[0].QuantityDelta);
            Assert.Equal(2, movements[0].QuantityAfter);
            Assert.Equal("Retry", await verifyDb.DeviceMessageInbox.OrderBy(row => row.MessageId)
                .Select(row => row.Status).LastAsync());
        }

        // Allow the scheduled retry immediately after the synthetic completion failure.
        await using (var replayDb = new AppDbContext(options))
        {
            await replayDb.Database.ExecuteSqlRawAsync("DROP TRIGGER inbox_completion_failure;");
            Assert.Equal(1, await replayDb.DeviceMessageInbox
                .Where(item => item.MachineId == machineId && item.MessageId == 3)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.NextAttemptAt, DateTime.UtcNow.AddSeconds(-1))));
        }
        Assert.True(await server.ProcessNextInboundMessageAsync());
        await using (var replayVerification = new AppDbContext(options))
        {
            Assert.Equal(1, await replayVerification.SaleRecords.CountAsync(item => item.TransactionId == transactionId));
            Assert.Equal(1, await replayVerification.InventoryMovements.CountAsync(item => item.MachineId == machineId));
            Assert.Equal(2, (await replayVerification.MachineProductSlots.AsNoTracking().SingleAsync(item => item.MachineId == machineId)).CurrentStock);
            Assert.Equal("Processed", (await replayVerification.DeviceMessageInbox.AsNoTracking().SingleAsync(item => item.MachineId == machineId && item.MessageId == 3)).Status);
        }

        controller.HttpContext.Request.Headers["x-request-id"] = "synthetic-rejected-payment-flow";
        using var rejectedWebhookDocument = JsonDocument.Parse(
            "{\"type\":\"payment\",\"action\":\"payment.updated\",\"data\":{\"id\":\"123456790\"}}");
        var rejectedWebhookResult = Assert.IsType<OkObjectResult>(
            await controller.WebhookMercadoPago(rejectedWebhookDocument.RootElement.Clone()));
        Assert.Contains("accepted", JsonSerializer.Serialize(rejectedWebhookResult.Value));
        var rejectedProcessingTask = processor.ProcessNextAsync(CancellationToken.None);
        using var denialCommand = JsonDocument.Parse(
            await ReceiveWebSocketTextAsync(offlineMachineSocket, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("VENDA_NEGADA", denialCommand.RootElement.GetProperty("data").GetString());
        await SendDeviceAckAsync(offlineMachineSocket, offlineSerial, denialCommand.RootElement.GetProperty("id").GetInt32());
        Assert.True(await rejectedProcessingTask.WaitAsync(TimeSpan.FromSeconds(5)));

        await SendDeviceMessageAsync(offlineMachineSocket, offlineSerial, 2, new
        {
            command = "pool",
            type = "vend_denied",
            transactionId = rejectedTransactionId
        });
        await ProcessNextDeviceMessageAsync(server, offlineMachineSocket, offlineSerial);
        await SendDeviceMessageAsync(offlineMachineSocket, offlineSerial, 3, new
        {
            command = "pool",
            type = "end_session",
            transactionId = rejectedTransactionId
        });
        await ProcessNextDeviceMessageAsync(server, offlineMachineSocket, offlineSerial);
        await using (var verifyDb = new AppDbContext(options))
        {
            var session = await verifyDb.MachineSessions.AsNoTracking().SingleAsync(item => item.Id == rejectedSessionId);
            var transaction = await verifyDb.PaymentTransactions.AsNoTracking()
                .SingleAsync(item => item.Id == rejectedTransactionId);
            var command = await verifyDb.TelemetryCommands.AsNoTracking()
                .SingleAsync(item => item.SessionId == rejectedSessionId);
            Assert.Equal(MachineSessionStates.Denied, session.State);
            Assert.Equal("Rejected", transaction.Status);
            Assert.Equal("acknowledged", command.Status);
            Assert.Empty(await verifyDb.SaleRecords.AsNoTracking()
                .Where(sale => sale.TransactionId == rejectedTransactionId).ToListAsync());
        }

        processingDb.PaymentTransactions.Add(new PaymentTransaction
        {
            Id = retryTransactionId,
            CompanyId = companyId,
            MachineId = offlineMachineId,
            Amount = 7m,
            ApplicationFee = 0.35m,
            Status = "Pending",
            MercadoPagoPaymentId = retryProviderPaymentId,
            SendTelemetryToMachine = true
        });
        processingDb.MachineSessions.Add(new MachineSession
        {
            Id = retrySessionId,
            CompanyId = companyId,
            MachineId = offlineMachineId,
            TransactionId = retryTransactionId,
            State = MachineSessionStates.PaymentPending,
            ItemNumber = 1,
            AmountCents = 700,
            PaymentDeadlineAt = DateTime.UtcNow.AddMinutes(5)
        });
        await processingDb.SaveChangesAsync();

        controller.HttpContext.Request.Headers["x-request-id"] = "synthetic-retry-then-delivery-failure";
        using var retryWebhookDocument = JsonDocument.Parse(
            "{\"type\":\"payment\",\"action\":\"payment.updated\",\"data\":{\"id\":\"123456792\"}}");
        var retryWebhookResult = Assert.IsType<OkObjectResult>(
            await controller.WebhookMercadoPago(retryWebhookDocument.RootElement.Clone()));
        Assert.Contains("accepted", JsonSerializer.Serialize(retryWebhookResult.Value));
        Assert.True(await processor.ProcessNextAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        Guid retryInboxId;
        await using (var verifyDb = new AppDbContext(options))
        {
            var session = await verifyDb.MachineSessions.AsNoTracking().SingleAsync(item => item.Id == retrySessionId);
            var transaction = await verifyDb.PaymentTransactions.AsNoTracking()
                .SingleAsync(item => item.Id == retryTransactionId);
            var inbox = await verifyDb.WebhookInboxMessages.AsNoTracking()
                .SingleAsync(item => item.PaymentId == retryProviderPaymentId);
            retryInboxId = inbox.Id;
            Assert.Equal(MachineSessionStates.PaymentPending, session.State);
            Assert.Equal("Pending", transaction.Status);
            Assert.Equal("Retry", inbox.Status);
            Assert.Empty(await verifyDb.TelemetryCommands.AsNoTracking()
                .Where(command => command.SessionId == retrySessionId).ToListAsync());
        }

        await processingDb.WebhookInboxMessages.Where(message => message.Id == retryInboxId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(message => message.NextAttemptAt, (DateTime?)null));
        var retryApprovalTask = processor.ProcessNextAsync(CancellationToken.None);
        using var retryApprovalCommand = JsonDocument.Parse(
            await ReceiveWebSocketTextAsync(offlineMachineSocket, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("VENDA_APROVADA", retryApprovalCommand.RootElement.GetProperty("data").GetString());
        await SendDeviceAckAsync(
            offlineMachineSocket,
            offlineSerial,
            retryApprovalCommand.RootElement.GetProperty("id").GetInt32());
        Assert.True(await retryApprovalTask.WaitAsync(TimeSpan.FromSeconds(5)));

        await SendDeviceMessageAsync(offlineMachineSocket, offlineSerial, 4, new
        {
            command = "pool",
            type = "vend_approved",
            transactionId = retryTransactionId
        });
        await ProcessNextDeviceMessageAsync(server, offlineMachineSocket, offlineSerial);
        await SendDeviceMessageAsync(offlineMachineSocket, offlineSerial, 5, new
        {
            command = "vend",
            type = "failure",
            reason = "out_of_product",
            transactionId = retryTransactionId
        });
        await ProcessNextDeviceMessageAsync(server, offlineMachineSocket, offlineSerial);
        await SendDeviceMessageAsync(offlineMachineSocket, offlineSerial, 6, new
        {
            command = "pool",
            type = "end_session",
            transactionId = retryTransactionId
        });
        await ProcessNextDeviceMessageAsync(server, offlineMachineSocket, offlineSerial);

        await using (var verifyDb = new AppDbContext(options))
        {
            var session = await verifyDb.MachineSessions.AsNoTracking().SingleAsync(item => item.Id == retrySessionId);
            var transaction = await verifyDb.PaymentTransactions.AsNoTracking()
                .SingleAsync(item => item.Id == retryTransactionId);
            var failure = await verifyDb.DeliveryFailures.AsNoTracking()
                .SingleAsync(item => item.SessionId == retrySessionId);
            var refund = await verifyDb.OutboxMessages.AsNoTracking()
                .SingleAsync(item => item.SessionId == retrySessionId && item.MessageType == "refund_payment");

            Assert.Equal(MachineSessionStates.Refunded, session.State);
            Assert.NotNull(session.ClosedAt);
            Assert.Equal("Refunded", transaction.Status);
            Assert.Equal("out_of_product", failure.Reason);
            Assert.Equal("processed", refund.Status);
            Assert.Equal(1, refund.Attempts);
            Assert.Empty(await verifyDb.SaleRecords.AsNoTracking()
                .Where(sale => sale.TransactionId == retryTransactionId).ToListAsync());
            Assert.Equal(2, providerHttpClientFactory.GetPaymentRequestCount(retryProviderPaymentId));
            Assert.Equal(1, providerHttpClientFactory.RefundRequestCount);
        }

        processingDb.PaymentTransactions.Add(new PaymentTransaction
        {
            Id = offlineTransactionId,
            CompanyId = companyId,
            MachineId = offlineMachineId,
            Amount = 7m,
            ApplicationFee = 0.35m,
            Status = "Pending",
            MercadoPagoPaymentId = offlineProviderPaymentId,
            SendTelemetryToMachine = true
        });
        processingDb.MachineSessions.Add(new MachineSession
        {
            Id = offlineSessionId,
            CompanyId = companyId,
            MachineId = offlineMachineId,
            TransactionId = offlineTransactionId,
            State = MachineSessionStates.PaymentPending,
            ItemNumber = 1,
            AmountCents = 700,
            PaymentDeadlineAt = DateTime.UtcNow.AddMinutes(5)
        });
        await processingDb.SaveChangesAsync();

        await offlineMachineSocket.CloseOutputAsync(
            WebSocketCloseStatus.NormalClosure,
            "simulate machine offline before provider approval",
            CancellationToken.None);
        await WaitForMachineOnlineStateAsync(options, offlineMachineId, expectedOnline: false, TimeSpan.FromSeconds(5));
        controller.HttpContext.Request.Headers["x-request-id"] = "synthetic-offline-machine-payment-flow";
        using var offlineWebhookDocument = JsonDocument.Parse(
            "{\"type\":\"payment\",\"action\":\"payment.updated\",\"data\":{\"id\":\"123456791\"}}");
        var offlineWebhookResult = Assert.IsType<OkObjectResult>(
            await controller.WebhookMercadoPago(offlineWebhookDocument.RootElement.Clone()));
        Assert.Contains("accepted", JsonSerializer.Serialize(offlineWebhookResult.Value));
        Assert.True(await processor.ProcessNextAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));

        await using (var verifyDb = new AppDbContext(options))
        {
            var session = await verifyDb.MachineSessions.AsNoTracking().SingleAsync(item => item.Id == offlineSessionId);
            var transaction = await verifyDb.PaymentTransactions.AsNoTracking()
                .SingleAsync(item => item.Id == offlineTransactionId);
            var command = await verifyDb.TelemetryCommands.AsNoTracking()
                .SingleAsync(item => item.SessionId == offlineSessionId);
            var refund = await verifyDb.OutboxMessages.AsNoTracking()
                .SingleAsync(item => item.SessionId == offlineSessionId && item.MessageType == "refund_payment");

            Assert.Equal(MachineSessionStates.Refunded, session.State);
            Assert.Equal("Refunded", transaction.Status);
            Assert.Equal("machine_offline", command.Error);
            Assert.Equal("failed", command.Status);
            Assert.Equal("processed", refund.Status);
            Assert.Equal(1, refund.Attempts);
            Assert.DoesNotContain(await verifyDb.SaleRecords.AsNoTracking().ToListAsync(), sale => sale.TransactionId == offlineTransactionId);
            Assert.Equal(2, providerHttpClientFactory.RefundRequestCount);
        }

        var shutdown = server.Manager.StopAsync(CancellationToken.None);
        var closeBuffer = new byte[256];
        var closeFrame = await machineSocket.ReceiveAsync(new ArraySegment<byte>(closeBuffer), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WebSocketMessageType.Close, closeFrame.MessageType);
        await machineSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "integration complete", CancellationToken.None);
        await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SimulatorReconnectsToNewTelemetryInstanceAfterGracefulShutdown()
    {
        var connectionString = $"Data Source=telemetry-simulator-reconnect-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var databaseKeeper = new SqliteConnection(connectionString);
        await databaseKeeper.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
        var companyId = Guid.NewGuid();
        var deviceCredential = DeviceCredentialService.Generate();
        await using (var db = new AppDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            var company = new Company
            {
                Id = companyId,
                Name = "Empresa de reconexão do simulador",
                ActivationStatus = CompanyActivationStatuses.Active
            };
            db.Companies.Add(company);
            db.Machines.Add(new Machine
            {
                Id = "machine-simulator-reconnect",
                Name = "Máquina simulada",
                SerialNumber = "SERIAL-SIMULATOR-RECONNECT",
                NormalizedSerialNumber = "SERIAL-SIMULATOR-RECONNECT",
                CompanyId = companyId,
                TelemetryCredentialHash = DeviceCredentialService.Hash(deviceCredential)
            });
            await db.SaveChangesAsync();
        }

        await using var firstInstance = await RunningTelemetryServer.StartAsync(
            connectionString,
            requireDeviceCredentials: true);
        var port = firstInstance.WebSocketUri.Port;
        using var simulatorShutdown = new CancellationTokenSource();
        var simulator = new MachineSimulator(
            new CliOptions(
                "SERIAL-SIMULATOR-RECONNECT",
                firstInstance.WebSocketUri,
                20,
                Reconnect: true,
                TimeSpan.FromHours(1),
                Headless: true)
            {
                DeviceCredential = deviceCredential
            },
            new SimulatorLog());
        var simulatorTask = simulator.RunAsync(simulatorShutdown.Token);

        try
        {
            await firstInstance.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForSimulatorConnectionAsync(simulator, TimeSpan.FromSeconds(5));
            var oldNodeId = firstInstance.Manager.NodeId;

            var firstShutdown = firstInstance.Manager.StopAsync(CancellationToken.None);
            await firstShutdown.WaitAsync(TimeSpan.FromSeconds(5));
            await firstInstance.RequestCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitForSimulatorStateAsync(simulator, SimulatorState.Disconnected, TimeSpan.FromSeconds(3));
            await firstInstance.StopHostAsync();

            await using var newInstance = await RunningTelemetryServer.StartAsync(
                connectionString,
                port,
                requireDeviceCredentials: true);
            await newInstance.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForSimulatorConnectionAsync(simulator, TimeSpan.FromSeconds(5));

            MachineConnectionState? state = null;
            var ownerDeadline = DateTime.UtcNow.AddSeconds(5);
            do
            {
                await using var db = new AppDbContext(options);
                state = await db.MachineConnectionStates.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.MachineId == "machine-simulator-reconnect");
                if (state?.OwnerNodeId == newInstance.Manager.NodeId) break;
                await Task.Delay(25);
            } while (DateTime.UtcNow < ownerDeadline);

            Assert.NotNull(state);
            Assert.True(state.IsOnline);
            Assert.Equal(companyId, state.CompanyId);
            Assert.Equal(newInstance.Manager.NodeId, state.OwnerNodeId);
            Assert.NotEqual(oldNodeId, state.OwnerNodeId);
        }
        finally
        {
            simulatorShutdown.Cancel();
            await simulatorTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task SimulatorReconnectsToNewTelemetryInstanceAfterGracefulShutdownWithPostgres()
    {
        var configuredConnection = Environment.GetEnvironmentVariable("VENDMACHINES_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(configuredConnection))
        {
            if (string.Equals(
                    Environment.GetEnvironmentVariable("VENDMACHINES_REQUIRE_TEST_POSTGRES"),
                    "true",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("O teste de continuidade WebSocket exige PostgreSQL quando VENDMACHINES_REQUIRE_TEST_POSTGRES=true.");
            return;
        }

        var databaseName = $"vendmachines_simulator_reconnect_{Guid.NewGuid():N}";
        var adminConnection = new NpgsqlConnectionStringBuilder(configuredConnection) { Database = "postgres" };
        var databaseConnection = new NpgsqlConnectionStringBuilder(configuredConnection) { Database = databaseName };
        await using (var admin = new NpgsqlConnection(adminConnection.ConnectionString))
        {
            await admin.OpenAsync();
            await using var createDatabase = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin);
            await createDatabase.ExecuteNonQueryAsync();
        }

        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(databaseConnection.ConnectionString)
                .Options;
            var companyId = Guid.NewGuid();
            var deviceCredential = DeviceCredentialService.Generate();
            await using (var db = new AppDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                db.Companies.Add(new Company
                {
                    Id = companyId,
                    Name = "Empresa de reconexão PostgreSQL",
                    ActivationStatus = CompanyActivationStatuses.Active
                });
                db.Machines.Add(new Machine
                {
                    Id = "machine-simulator-reconnect-postgres",
                    Name = "Máquina simulada PostgreSQL",
                    SerialNumber = "SERIAL-SIMULATOR-RECONNECT-PG",
                    NormalizedSerialNumber = "SERIAL-SIMULATOR-RECONNECT-PG",
                    CompanyId = companyId,
                    TelemetryCredentialHash = DeviceCredentialService.Hash(deviceCredential)
                });
                await db.SaveChangesAsync();
            }

            await using var firstInstance = await RunningTelemetryServer.StartAsync(
                databaseConnection.ConnectionString,
                requireDeviceCredentials: true,
                usePostgres: true);
            var port = firstInstance.WebSocketUri.Port;
            using var simulatorShutdown = new CancellationTokenSource();
            var simulator = new MachineSimulator(
                new CliOptions(
                    "SERIAL-SIMULATOR-RECONNECT-PG",
                    firstInstance.WebSocketUri,
                    20,
                    Reconnect: true,
                    TimeSpan.FromHours(1),
                    Headless: true)
                {
                    DeviceCredential = deviceCredential
                },
                new SimulatorLog());
            var simulatorTask = simulator.RunAsync(simulatorShutdown.Token);

            try
            {
                await firstInstance.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await WaitForSimulatorConnectionAsync(simulator, TimeSpan.FromSeconds(5));
                var oldNodeId = firstInstance.Manager.NodeId;

                await firstInstance.Manager.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
                await firstInstance.RequestCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await WaitForSimulatorStateAsync(simulator, SimulatorState.Disconnected, TimeSpan.FromSeconds(3));
                await firstInstance.StopHostAsync();

                await using var newInstance = await RunningTelemetryServer.StartAsync(
                    databaseConnection.ConnectionString,
                    port,
                    requireDeviceCredentials: true,
                    usePostgres: true);
                await newInstance.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await WaitForSimulatorConnectionAsync(simulator, TimeSpan.FromSeconds(5));

                MachineConnectionState? state = null;
                var ownerDeadline = DateTime.UtcNow.AddSeconds(5);
                do
                {
                    await using var db = new AppDbContext(options);
                    state = await db.MachineConnectionStates.AsNoTracking()
                        .SingleOrDefaultAsync(item => item.MachineId == "machine-simulator-reconnect-postgres");
                    if (state?.OwnerNodeId == newInstance.Manager.NodeId) break;
                    await Task.Delay(25);
                } while (DateTime.UtcNow < ownerDeadline);

                Assert.NotNull(state);
                Assert.True(state.IsOnline);
                Assert.Equal(companyId, state.CompanyId);
                Assert.Equal(newInstance.Manager.NodeId, state.OwnerNodeId);
                Assert.NotEqual(oldNodeId, state.OwnerNodeId);
            }
            finally
            {
                simulatorShutdown.Cancel();
                await simulatorTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            await using var admin = new NpgsqlConnection(adminConnection.ConnectionString);
            await admin.OpenAsync();
            await using var dropDatabase = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin);
            await dropDatabase.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task CommandFromAnotherNodeIsSentOnceByTheSocketOwner()
    {
        var connectionString = $"Data Source=telemetry-dispatch-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var databaseKeeper = new SqliteConnection(connectionString);
        await databaseKeeper.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
        await using (var db = new AppDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            var company = new Company { Id = Guid.NewGuid(), Name = "Empresa de roteamento", ActivationStatus = CompanyActivationStatuses.Active };
            db.Companies.Add(company);
            db.Machines.Add(new Machine
            {
                Id = "machine-route", Name = "Máquina", SerialNumber = "SERIAL-ROUTE",
                NormalizedSerialNumber = "SERIAL-ROUTE", CompanyId = company.Id,
                TelemetryCredentialHash = DeviceCredentialService.Hash(DeviceCredentialService.Generate())
            });
            await db.SaveChangesAsync();
        }

        await using var owner = CreateNode(connectionString);
        await using var caller = CreateNode(connectionString);
        await owner.Manager.StartAsync(CancellationToken.None);
        await caller.Manager.StartAsync(CancellationToken.None);

        using var socket = new ScriptedWebSocket();
        socket.SendText("{\"id\":1,\"type\":\"start\",\"target\":\"SERIAL-ROUTE\"}");
        var handshake = owner.Manager.HandleDeviceAsync(socket, CancellationToken.None);
        await socket.FirstTextSent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var send = caller.Manager.SendCommandAsync("SERIAL-ROUTE", "MDB_STATUS");
        string? commandFrame = null;
        var commandDeadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < commandDeadline)
        {
            commandFrame = socket.SentMessages.FirstOrDefault(message => message.Contains("\"data\":\"MDB_STATUS\"", StringComparison.Ordinal));
            if (commandFrame != null) break;
            await Task.Delay(25);
        }

        Assert.NotNull(commandFrame);
        using (var command = JsonDocument.Parse(commandFrame!))
            socket.SendText($"{{\"type\":\"ack\",\"id\":{command.RootElement.GetProperty("id").GetInt32()}}}");

        var result = await send.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.Success);
        Assert.Equal(1, socket.SentMessages.Count(message => message.Contains("\"data\":\"MDB_STATUS\"", StringComparison.Ordinal)));

        Assert.True(await caller.Manager.IsOnlineAsync("SERIAL-ROUTE"));
        Assert.Contains("SERIAL-ROUTE", await caller.Manager.GetOnlineSerialsAsync(["SERIAL-ROUTE"]));
        var previousStatusCommandCount = socket.SentMessages.Count(message =>
            message.Contains("\"type\":\"msg\"", StringComparison.Ordinal) &&
            message.Contains("\"data\":\"MDB_STATUS\"", StringComparison.Ordinal));
        var statusTimeout = TimeSpan.FromSeconds(15);
        var statusRequest = caller.Manager.SendStatusRequestAsync("SERIAL-ROUTE", statusTimeout);
        string? statusFrame = null;
        var statusDeadline = DateTime.UtcNow.Add(statusTimeout);
        while (DateTime.UtcNow < statusDeadline)
        {
            statusFrame = socket.SentMessages.LastOrDefault(message =>
                message.Contains("\"type\":\"msg\"", StringComparison.Ordinal) &&
                message.Contains("\"data\":\"MDB_STATUS\"", StringComparison.Ordinal));
            if (statusFrame != null && socket.SentMessages.Count(message =>
                    message.Contains("\"type\":\"msg\"", StringComparison.Ordinal) &&
                    message.Contains("\"data\":\"MDB_STATUS\"", StringComparison.Ordinal)) > previousStatusCommandCount)
                break;
            await Task.Delay(25);
        }
        Assert.NotNull(statusFrame);
        using (var statusCommand = JsonDocument.Parse(statusFrame!))
        {
            var messageId = statusCommand.RootElement.GetProperty("id").GetInt32();
            socket.SendText($"{{\"id\":{messageId},\"type\":\"msg\",\"target\":\"SERIAL-ROUTE\",\"data\":{{\"command\":\"status\",\"status\":\"idle_state\"}}}}");
        }
        var statusResult = await statusRequest.WaitAsync(statusTimeout + TimeSpan.FromSeconds(5));
        Assert.True(statusResult.Success);
        Assert.Equal(MdbStatuses.Idle, statusResult.Status);

        socket.SendClose();
        await handshake.WaitAsync(TimeSpan.FromSeconds(5));
        await owner.Manager.StopAsync(CancellationToken.None);
        await caller.Manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CredentialDisconnectIsDispatchedToRemoteOwnerEvenWhenLastSeenIsStale()
    {
        var connectionString = $"Data Source=telemetry-disconnect-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var databaseKeeper = new SqliteConnection(connectionString);
        await databaseKeeper.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
        Guid companyId;
        await using (var db = new AppDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            var company = new Company { Id = Guid.NewGuid(), Name = "Empresa de desconexão", ActivationStatus = CompanyActivationStatuses.Active };
            companyId = company.Id;
            db.Companies.Add(company);
            db.Machines.Add(new Machine
            {
                Id = "machine-disconnect-route", Name = "Máquina", SerialNumber = "SERIAL-DISCONNECT",
                NormalizedSerialNumber = "SERIAL-DISCONNECT", CompanyId = company.Id,
                TelemetryCredentialHash = DeviceCredentialService.Hash(DeviceCredentialService.Generate())
            });
            await db.SaveChangesAsync();
        }

        await using var owner = CreateNode(connectionString);
        await using var caller = CreateNode(connectionString);
        await owner.Manager.StartAsync(CancellationToken.None);
        await caller.Manager.StartAsync(CancellationToken.None);

        using var socket = new ScriptedWebSocket();
        socket.SendText("{\"id\":1,\"type\":\"start\",\"target\":\"SERIAL-DISCONNECT\"}");
        var handshake = owner.Manager.HandleDeviceAsync(socket, CancellationToken.None);
        await socket.FirstTextSent.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await using (var db = new AppDbContext(options))
        {
            var state = await db.MachineConnectionStates.SingleAsync(x => x.MachineId == "machine-disconnect-route");
            Assert.Equal(companyId, state.CompanyId);
            Assert.Equal(owner.Manager.NodeId, state.OwnerNodeId);
            state.LastSeenAt = DateTime.UtcNow.AddMinutes(-5);
            await db.SaveChangesAsync();
        }

        var disconnect = await caller.Manager.DisconnectMachineAsync(
            "SERIAL-DISCONNECT", "Credencial rotacionada.").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(disconnect.Success);
        await handshake.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, socket.CloseStatus);
        Assert.True(socket.WasAborted);
        Assert.False(await caller.Manager.IsOnlineAsync("SERIAL-DISCONNECT"));

        await using (var db = new AppDbContext(options))
        {
            var dispatch = await db.TelemetryCommandDispatches.SingleAsync(x => x.Operation == "Disconnect");
            Assert.Equal(owner.Manager.NodeId, dispatch.OwnerNodeId);
            Assert.Equal("Completed", dispatch.Status);
            Assert.True(dispatch.Success);
            var state = await db.MachineConnectionStates.SingleAsync(x => x.MachineId == "machine-disconnect-route");
            Assert.False(state.IsOnline);
            Assert.Null(state.OwnerNodeId);
        }

        await owner.Manager.StopAsync(CancellationToken.None);
        await caller.Manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StaleCredentialDisconnectDoesNotCloseReplacementSocketOnSameNode()
    {
        var connectionString = $"Data Source=telemetry-disconnect-replacement-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var databaseKeeper = new SqliteConnection(connectionString);
        await databaseKeeper.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
        Guid companyId;
        await using (var db = new AppDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            var company = new Company { Id = Guid.NewGuid(), Name = "Empresa de substituição", ActivationStatus = CompanyActivationStatuses.Active };
            companyId = company.Id;
            db.Companies.Add(company);
            db.Machines.Add(new Machine
            {
                Id = "machine-disconnect-replacement", Name = "Máquina", SerialNumber = "SERIAL-DISCONNECT-REPLACEMENT",
                NormalizedSerialNumber = "SERIAL-DISCONNECT-REPLACEMENT", CompanyId = company.Id,
                TelemetryCredentialHash = DeviceCredentialService.Hash(DeviceCredentialService.Generate())
            });
            await db.SaveChangesAsync();
        }

        await using var owner = CreateNode(connectionString);
        await owner.Manager.StartAsync(CancellationToken.None);

        using var oldSocket = new ScriptedWebSocket();
        oldSocket.SendText("{\"id\":1,\"type\":\"start\",\"target\":\"SERIAL-DISCONNECT-REPLACEMENT\"}");
        var oldHandshake = owner.Manager.HandleDeviceAsync(oldSocket, CancellationToken.None);
        await oldSocket.FirstTextSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        string oldOwnerConnectionId;
        await using (var db = new AppDbContext(options))
            oldOwnerConnectionId = (await db.MachineConnectionStates.SingleAsync(x => x.MachineId == "machine-disconnect-replacement")).OwnerConnectionId!;

        oldSocket.SendClose();
        await oldHandshake.WaitAsync(TimeSpan.FromSeconds(5));

        using var replacementSocket = new ScriptedWebSocket();
        replacementSocket.SendText("{\"id\":2,\"type\":\"start\",\"target\":\"SERIAL-DISCONNECT-REPLACEMENT\"}");
        var replacementHandshake = owner.Manager.HandleDeviceAsync(replacementSocket, CancellationToken.None);
        await replacementSocket.FirstTextSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        string replacementOwnerConnectionId;
        await using (var db = new AppDbContext(options))
        {
            var state = await db.MachineConnectionStates.SingleAsync(x => x.MachineId == "machine-disconnect-replacement");
            Assert.Equal(companyId, state.CompanyId);
            Assert.Equal(owner.Manager.NodeId, state.OwnerNodeId);
            replacementOwnerConnectionId = state.OwnerConnectionId!;
            Assert.NotEqual(oldOwnerConnectionId, replacementOwnerConnectionId);

            var staleDispatch = new TelemetryCommandDispatch
            {
                CompanyId = companyId,
                MachineId = "machine-disconnect-replacement",
                OwnerNodeId = owner.Manager.NodeId,
                Operation = "Disconnect",
                PayloadJson = JsonSerializer.Serialize(new
                {
                    reason = "Credencial rotacionada.",
                    ownerConnectionId = oldOwnerConnectionId
                }),
                Status = "Pending",
                CreatedAt = DateTime.UtcNow
            };
            db.TelemetryCommandDispatches.Add(staleDispatch);
            await db.SaveChangesAsync();
        }

        TelemetryCommandDispatch? result = null;
        var dispatchDeadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < dispatchDeadline)
        {
            await using var db = new AppDbContext(options);
            result = await db.TelemetryCommandDispatches.SingleOrDefaultAsync(x => x.Operation == "Disconnect");
            if (result?.Status == "Failed") break;
            await Task.Delay(25);
        }

        Assert.NotNull(result);
        Assert.Equal("Failed", result.Status);
        Assert.Equal("owner_changed_before_send", result.Error);
        Assert.False(result.Success);
        Assert.True(owner.Manager.IsOnline("SERIAL-DISCONNECT-REPLACEMENT"));
        Assert.Equal(WebSocketState.Open, replacementSocket.State);

        replacementSocket.SendClose();
        await replacementHandshake.WaitAsync(TimeSpan.FromSeconds(5));
        await owner.Manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CancelledRemoteCredentialDisconnectCannotRemainQueuedForLaterExecution()
    {
        var connectionString = $"Data Source=telemetry-disconnect-cancel-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var databaseKeeper = new SqliteConnection(connectionString);
        await databaseKeeper.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
        var companyId = Guid.NewGuid();
        await using (var db = new AppDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.Companies.Add(new Company
            {
                Id = companyId,
                Name = "Empresa de cancelamento",
                ActivationStatus = CompanyActivationStatuses.Active
            });
            db.Machines.Add(new Machine
            {
                Id = "machine-disconnect-cancel",
                Name = "Máquina",
                SerialNumber = "SERIAL-DISCONNECT-CANCEL",
                NormalizedSerialNumber = "SERIAL-DISCONNECT-CANCEL",
                CompanyId = companyId
            });
            db.MachineConnectionStates.Add(new MachineConnectionState
            {
                MachineId = "machine-disconnect-cancel",
                CompanyId = companyId,
                IsOnline = true,
                LastSeenAt = DateTime.UtcNow,
                OwnerNodeId = "remote-owner-node",
                OwnerConnectionId = "remote-owner-connection",
                CommandRoutingVersion = TelemetryConnectionManager.CurrentCommandRoutingVersion
            });
            await db.SaveChangesAsync();
        }

        await using var caller = CreateNode(connectionString);
        using var cancellation = new CancellationTokenSource();
        var disconnect = caller.Manager.DisconnectMachineAsync(
            "SERIAL-DISCONNECT-CANCEL", "Credencial alterada.", cancellation.Token);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        TelemetryCommandDispatch? dispatch = null;
        while (DateTime.UtcNow < deadline)
        {
            await using var db = new AppDbContext(options);
            dispatch = await db.TelemetryCommandDispatches.SingleOrDefaultAsync(x => x.Operation == "Disconnect");
            if (dispatch != null) break;
            await Task.Delay(10);
        }

        Assert.NotNull(dispatch);
        Assert.Equal("Pending", dispatch.Status);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await disconnect);
        await using (var db = new AppDbContext(options))
        {
            var cancelled = await db.TelemetryCommandDispatches.SingleAsync(x => x.Id == dispatch.Id);
            Assert.Equal("Cancelled", cancelled.Status);
            Assert.Equal("dispatch_cancelled_before_send", cancelled.Error);
            Assert.NotNull(cancelled.CompletedAt);
        }
    }

    [Fact]
    public async Task PostgresCommandRoutingSurvivesOwnerShutdownAndDeviceReconnect()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("VENDMACHINES_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("VENDMACHINES_REQUIRE_TEST_POSTGRES"), "true", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("O teste de roteamento MDB entre instâncias exige PostgreSQL no CI.");
            return;
        }

        var databaseName = $"telemetry_dispatch_{Guid.NewGuid():N}";
        var adminBuilder = new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = "postgres" };
        var databaseBuilder = new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = databaseName };
        await using (var admin = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await admin.OpenAsync();
            await using var createDatabase = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin);
            await createDatabase.ExecuteNonQueryAsync();
        }

        try
        {
            var scopedConnectionString = databaseBuilder.ConnectionString;
            var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(scopedConnectionString).Options;
            await using (var db = new AppDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                var company = new Company
                {
                    Id = Guid.NewGuid(),
                    Name = "Empresa de roteamento PostgreSQL",
                    ActivationStatus = CompanyActivationStatuses.Active
                };
                db.Companies.Add(company);
                db.Machines.Add(new Machine
                {
                    Id = "machine-postgres-route",
                    Name = "Máquina PostgreSQL",
                    SerialNumber = "SERIAL-PG-ROUTE",
                    NormalizedSerialNumber = "SERIAL-PG-ROUTE",
                    CompanyId = company.Id
                });
                await db.SaveChangesAsync();
            }

            await using (var owner = CreatePostgresNode(scopedConnectionString))
            await using (var caller = CreatePostgresNode(scopedConnectionString))
            await using (var requester = CreatePostgresNode(scopedConnectionString))
            {
                await owner.Manager.StartAsync(CancellationToken.None);
                await caller.Manager.StartAsync(CancellationToken.None);
                await requester.Manager.StartAsync(CancellationToken.None);

                using var socket = new ScriptedWebSocket();
                socket.SendText("{\"id\":1,\"type\":\"start\",\"target\":\"SERIAL-PG-ROUTE\"}");
                var handshake = owner.Manager.HandleDeviceAsync(socket, CancellationToken.None);
                await socket.FirstTextSent.Task.WaitAsync(TimeSpan.FromSeconds(5));

                var routeDeadline = DateTime.UtcNow.AddSeconds(5);
                var ownerPersisted = false;
                while (DateTime.UtcNow < routeDeadline)
                {
                    await using var routeDb = new AppDbContext(options);
                    ownerPersisted = await routeDb.MachineConnectionStates.AsNoTracking().AnyAsync(state =>
                        state.MachineId == "machine-postgres-route" &&
                        state.IsOnline &&
                        state.OwnerNodeId == owner.Manager.NodeId);
                    if (ownerPersisted) break;
                    await Task.Delay(25);
                }
                Assert.True(ownerPersisted, "O socket precisa estar registrado no nó proprietário antes de testar o despacho entre nós.");

                var send = caller.Manager.SendCommandAsync("SERIAL-PG-ROUTE", "MDB_STATUS");
                string? commandFrame = null;
                var commandDeadline = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < commandDeadline)
                {
                    commandFrame = socket.SentMessages.FirstOrDefault(message =>
                        message.Contains("\"data\":\"MDB_STATUS\"", StringComparison.Ordinal));
                    if (commandFrame != null) break;
                    await Task.Delay(25);
                }

                Assert.NotNull(commandFrame);
                using (var command = JsonDocument.Parse(commandFrame!))
                    socket.SendText($"{{\"type\":\"ack\",\"id\":{command.RootElement.GetProperty("id").GetInt32()}}}");

                var result = await send.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(result.Success);
                Assert.Equal(1, socket.SentMessages.Count(message =>
                    message.Contains("\"data\":\"MDB_STATUS\"", StringComparison.Ordinal)));

                socket.SendText("{\"id\":301,\"type\":\"msg\",\"target\":\"SERIAL-PG-ROUTE\",\"data\":{\"command\":\"pool\",\"type\":\"begin_session\"}}");
                var inboundDeadline = DateTime.UtcNow.AddSeconds(5);
                var inboundAcknowledged = false;
                while (DateTime.UtcNow < inboundDeadline)
                {
                    inboundAcknowledged = socket.SentMessages.Any(message =>
                    {
                        using var frame = JsonDocument.Parse(message);
                        return frame.RootElement.TryGetProperty("type", out var type) && type.GetString() == "ack" &&
                               frame.RootElement.TryGetProperty("id", out var id) && id.GetInt32() == 301;
                    });
                    if (inboundAcknowledged) break;
                    await Task.Delay(25);
                }
                Assert.True(inboundAcknowledged, "A instância que mantém o socket precisa confirmar a mensagem recebida durante a sobreposição.");
                await using (var inboxDb = new AppDbContext(options))
                    Assert.Equal(1, await inboxDb.DeviceMessageInbox.CountAsync(row => row.MachineId == "machine-postgres-route" && row.MessageId == 301));

                await owner.Manager.StopAsync(CancellationToken.None);
                await handshake.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, socket.CloseStatus);

                using (var offlineDb = new AppDbContext(options))
                {
                    var offlineState = await offlineDb.MachineConnectionStates.AsNoTracking()
                        .SingleAsync(state => state.MachineId == "machine-postgres-route");
                    Assert.False(offlineState.IsOnline);
                    Assert.Null(offlineState.OwnerNodeId);
                }

                using var reconnectedSocket = new ScriptedWebSocket();
                reconnectedSocket.SendText("{\"id\":1,\"type\":\"start\",\"target\":\"SERIAL-PG-ROUTE\"}");
                var reconnectHandshake = caller.Manager.HandleDeviceAsync(reconnectedSocket, CancellationToken.None);
                await reconnectedSocket.FirstTextSent.Task.WaitAsync(TimeSpan.FromSeconds(5));

                var reconnectDeadline = DateTime.UtcNow.AddSeconds(5);
                var newOwnerPersisted = false;
                while (DateTime.UtcNow < reconnectDeadline)
                {
                    await using var routeDb = new AppDbContext(options);
                    newOwnerPersisted = await routeDb.MachineConnectionStates.AsNoTracking().AnyAsync(state =>
                        state.MachineId == "machine-postgres-route" &&
                        state.IsOnline &&
                        state.OwnerNodeId == caller.Manager.NodeId);
                    if (newOwnerPersisted) break;
                    await Task.Delay(25);
                }
                Assert.True(newOwnerPersisted, "Após reconectar, a propriedade deve apontar para o novo nó antes de rotear comandos.");

                var handoffSend = requester.Manager.SendCommandAsync("SERIAL-PG-ROUTE", "MDB_STATUS");
                string? handoffFrame = null;
                var handoffDeadline = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < handoffDeadline)
                {
                    handoffFrame = reconnectedSocket.SentMessages.FirstOrDefault(message =>
                        message.Contains("\"data\":\"MDB_STATUS\"", StringComparison.Ordinal));
                    if (handoffFrame != null) break;
                    await Task.Delay(25);
                }

                Assert.NotNull(handoffFrame);
                using (var command = JsonDocument.Parse(handoffFrame!))
                    reconnectedSocket.SendText($"{{\"type\":\"ack\",\"id\":{command.RootElement.GetProperty("id").GetInt32()}}}");

                var handoffResult = await handoffSend.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(handoffResult.Success);
                Assert.Equal(1, reconnectedSocket.SentMessages.Count(message =>
                    message.Contains("\"data\":\"MDB_STATUS\"", StringComparison.Ordinal)));

                reconnectedSocket.SendClose();
                await reconnectHandshake.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            await using var cleanup = new NpgsqlConnection(adminBuilder.ConnectionString);
            await cleanup.OpenAsync();
            await using var dropDatabase = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", cleanup);
            await dropDatabase.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task LegacyOnlineMachineWithoutOwnerRemainsVisibleAndReturnsExplicitUnsupportedRoute()
    {
        var connectionString = $"Data Source=telemetry-legacy-owner-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var databaseKeeper = new SqliteConnection(connectionString);
        await databaseKeeper.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
        await using (var db = new AppDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            var company = new Company { Id = Guid.NewGuid(), Name = "Empresa legada", ActivationStatus = CompanyActivationStatuses.Active };
            db.Companies.Add(company);
            db.Machines.Add(new Machine
            {
                Id = "machine-legacy-owner", Name = "Máquina legada", SerialNumber = "SERIAL-LEGACY",
                NormalizedSerialNumber = "SERIAL-LEGACY", CompanyId = company.Id
            });
            db.MachineConnectionStates.Add(new MachineConnectionState
            {
                MachineId = "machine-legacy-owner", CompanyId = company.Id, IsOnline = true,
                ConnectionId = "legacy-connection", LastSeenAt = DateTime.UtcNow, OwnerNodeId = null
            });
            await db.SaveChangesAsync();
        }

        await using var apiNode = CreateNode(connectionString);
        Assert.True(await apiNode.Manager.IsOnlineAsync("SERIAL-LEGACY"));
        Assert.False(await apiNode.Manager.HasCommandRouteAsync("SERIAL-LEGACY"));
        Assert.Contains("SERIAL-LEGACY", await apiNode.Manager.GetOnlineSerialsAsync(["SERIAL-LEGACY"]));

        var command = await apiNode.Manager.SendCommandAsync("SERIAL-LEGACY", "FECHAR_SESSAO");
        var status = await apiNode.Manager.SendStatusRequestAsync("SERIAL-LEGACY", TimeSpan.FromSeconds(1));
        Assert.Equal("machine_owner_unknown", command.Error);
        Assert.Equal("machine_owner_unknown", status.Error);
        Assert.False(command.Success);
        Assert.False(status.Success);

        await using var verifyDb = new AppDbContext(options);
        Assert.Empty(await verifyDb.TelemetryCommandDispatches.ToListAsync());
        var preservedConnection = await verifyDb.MachineConnectionStates.SingleAsync(state => state.MachineId == "machine-legacy-owner");
        Assert.True(preservedConnection.IsOnline);
        Assert.Equal("legacy-connection", preservedConnection.ConnectionId);
        Assert.Null(preservedConnection.OwnerNodeId);
    }

    [Fact]
    public async Task OnlineOwnerWithoutCommandRoutingCapabilityDoesNotReceiveDispatch()
    {
        var connectionString = $"Data Source=telemetry-legacy-command-route-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var databaseKeeper = new SqliteConnection(connectionString);
        await databaseKeeper.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
        var companyId = Guid.NewGuid();
        var oldNodeId = Guid.NewGuid().ToString("N");
        await using (var db = new AppDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.Companies.Add(new Company { Id = companyId, Name = "Empresa durante atualização", ActivationStatus = CompanyActivationStatuses.Active });
            db.Machines.Add(new Machine
            {
                Id = "machine-legacy-command-route", Name = "Máquina durante atualização",
                SerialNumber = "SERIAL-LEGACY-ROUTE", NormalizedSerialNumber = "SERIAL-LEGACY-ROUTE", CompanyId = companyId
            });
            db.MachineConnectionStates.Add(new MachineConnectionState
            {
                MachineId = "machine-legacy-command-route", CompanyId = companyId, IsOnline = true,
                ConnectionId = "old-connection", OwnerNodeId = oldNodeId, OwnerConnectionId = "old-connection",
                CommandRoutingVersion = null, LastSeenAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        await using var newNode = CreateNode(connectionString);
        Assert.True(await newNode.Manager.IsOnlineAsync("SERIAL-LEGACY-ROUTE"));
        Assert.False(await newNode.Manager.HasCommandRouteAsync("SERIAL-LEGACY-ROUTE"));

        var command = await newNode.Manager.SendCommandAsync("SERIAL-LEGACY-ROUTE", "FECHAR_SESSAO");
        var status = await newNode.Manager.SendStatusRequestAsync("SERIAL-LEGACY-ROUTE", TimeSpan.FromSeconds(1));
        Assert.Equal("machine_owner_unknown", command.Error);
        Assert.Equal("machine_owner_unknown", status.Error);

        await using var verifyDb = new AppDbContext(options);
        Assert.Empty(await verifyDb.TelemetryCommandDispatches.ToListAsync());
        var state = await verifyDb.MachineConnectionStates.SingleAsync(item => item.MachineId == "machine-legacy-command-route");
        Assert.True(state.IsOnline);
        Assert.Equal(oldNodeId, state.OwnerNodeId);
        Assert.Null(state.CommandRoutingVersion);
    }

    private static Task SendStartFrameAsync(WebSocket socket)
        => SendStartFrameAsync(socket, "SERIAL-KESTREL-RECONNECT");

    private static async Task SendStartFrameAsync(WebSocket socket, string serial)
    {
        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { id = 1, type = "start", target = serial }));
        await socket.SendAsync(payload, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task SendDeviceAckAsync(WebSocket socket, string serial, int messageId)
    {
        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { id = messageId, target = serial, type = "ack" }));
        await socket.SendAsync(payload, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task SendDeviceMessageAsync(WebSocket socket, string serial, int messageId, object data)
    {
        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { id = messageId, target = serial, type = "msg", data }));
        await socket.SendAsync(payload, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task ProcessNextDeviceMessageAsync(
        RunningTelemetryServer server,
        WebSocket socket,
        string serial)
    {
        using var acknowledgement = JsonDocument.Parse(
            await ReceiveWebSocketTextAsync(socket, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("ack", acknowledgement.RootElement.GetProperty("type").GetString());
        Assert.Equal(serial, acknowledgement.RootElement.GetProperty("target").GetString());
        Assert.True(await server.ProcessNextInboundMessageAsync());
    }

    private static async Task WaitForMachineOnlineStateAsync(
        DbContextOptions<AppDbContext> options,
        string machineId,
        bool expectedOnline,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        do
        {
            await using var db = new AppDbContext(options);
            var state = await db.MachineConnectionStates.AsNoTracking()
                .SingleOrDefaultAsync(item => item.MachineId == machineId);
            if (state?.IsOnline == expectedOnline) return;
            await Task.Delay(25);
        } while (DateTime.UtcNow < deadline);

        throw new TimeoutException($"A máquina {machineId} não chegou ao estado online={expectedOnline}.");
    }

    private static async Task WaitForMachineConnectionStateCountAsync(
        DbContextOptions<AppDbContext> options,
        int expectedCount,
        bool online,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        do
        {
            await using var db = new AppDbContext(options);
            if (await db.MachineConnectionStates.CountAsync(state => state.IsOnline == online) == expectedCount)
                return;
            await Task.Delay(25);
        } while (DateTime.UtcNow < deadline);

        await using var diagnosticDb = new AppDbContext(options);
        var onlineCount = await diagnosticDb.MachineConnectionStates.CountAsync(state => state.IsOnline);
        var totalCount = await diagnosticDb.MachineConnectionStates.CountAsync();
        throw new TimeoutException(
            $"A frota não chegou ao estado esperado (online={online}) dentro do prazo. Estados online: {onlineCount}; estados totais: {totalCount}.");
    }

    private static async Task<string> ReceiveWebSocketTextAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException("O servidor fechou o socket antes de enviar a resposta esperada.");
            if (result.MessageType != WebSocketMessageType.Text) continue;
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return Encoding.UTF8.GetString(message.ToArray());
        }
    }

    private static async Task WaitForSimulatorConnectionAsync(MachineSimulator simulator, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            if (simulator.State == SimulatorState.Connected && simulator.IsConnected) return;
            await Task.Delay(25);
        }

        throw new TimeoutException($"O simulador não conectou dentro do limite. Estado atual: {simulator.State}.");
    }

    private static async Task WaitForSimulatorStateAsync(MachineSimulator simulator, SimulatorState state, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            if (simulator.State == state) return;
            await Task.Delay(25);
        }

        throw new TimeoutException($"O simulador não chegou ao estado {state}. Estado atual: {simulator.State}.");
    }

    private sealed class CapturingTelemetryLogger(ConcurrentQueue<string> entries) : ILogger<TelemetryConnectionManager>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            entries.Enqueue($"{logLevel}: {formatter(state, exception)} {exception?.GetType().Name}: {exception?.Message}");
        }
    }

    private sealed class RunningTelemetryServer : IAsyncDisposable
    {
        private readonly WebApplication _application;
        private int _stopped;

        private RunningTelemetryServer(
            WebApplication application,
            TelemetryConnectionManager manager,
            Uri webSocketUri,
            TaskCompletionSource requestStarted,
            TaskCompletionSource requestCompleted,
            ConcurrentQueue<string> managerLogEntries)
        {
            _application = application;
            Manager = manager;
            WebSocketUri = webSocketUri;
            RequestStarted = requestStarted;
            RequestCompleted = requestCompleted;
            ManagerLogEntries = managerLogEntries;
        }

        public TelemetryConnectionManager Manager { get; }
        public Uri WebSocketUri { get; }
        public TaskCompletionSource RequestStarted { get; }
        public TaskCompletionSource RequestCompleted { get; }
        public ConcurrentQueue<string> ManagerLogEntries { get; }
        public IServiceProvider ServiceProvider => _application.Services;

        public static async Task<RunningTelemetryServer> StartAsync(
            string connectionString,
            int port = 0,
            bool requireDeviceCredentials = false,
            bool usePostgres = false,
            IDeviceTelemetryMessageHandler? deviceTelemetryMessageHandler = null)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Development
            });
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
            builder.Services.AddDbContext<AppDbContext>(options =>
            {
                if (usePostgres) options.UseNpgsql(connectionString);
                else options.UseSqlite(connectionString);
            });
            if (deviceTelemetryMessageHandler != null)
            {
                builder.Services.AddSingleton<DeviceMessageInboxAdmissionGate>();
                builder.Services.AddScoped<DeviceMessageInboxService>();
                builder.Services.AddScoped<DeviceMessageInboxProcessor>();
                builder.Services.AddSingleton<IDeviceTelemetryMessageHandler>(deviceTelemetryMessageHandler);
            }
            var application = builder.Build();
            application.UseWebSockets();

            var managerLogEntries = new ConcurrentQueue<string>();
            var manager = new TelemetryConnectionManager(
                application.Services.GetRequiredService<IServiceScopeFactory>(),
                new TelemetryPanelHub(NullLogger<TelemetryPanelHub>.Instance),
                new CapturingTelemetryLogger(managerLogEntries),
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Telemetry:RequireDeviceCredentials"] = requireDeviceCredentials.ToString()
                    })
                    .Build());
            var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var requestCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            application.Map("/telemetria", async context =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                requestStarted.TrySetResult();
                try
                {
                    await manager.HandleDeviceAsync(socket, context.RequestAborted);
                }
                finally
                {
                    requestCompleted.TrySetResult();
                }
            });

            await application.StartAsync();
            var addresses = application.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses;
            var address = addresses?.SingleOrDefault(value => value.StartsWith("http://", StringComparison.Ordinal));
            if (address == null)
            {
                await application.DisposeAsync();
                throw new InvalidOperationException("O servidor WebSocket de teste não publicou uma porta local.");
            }

            var webSocketUri = new UriBuilder(address) { Scheme = "ws", Path = "/telemetria" }.Uri;
            return new RunningTelemetryServer(application, manager, webSocketUri, requestStarted, requestCompleted, managerLogEntries);
        }

        public async Task<bool> ProcessNextInboundMessageAsync()
        {
            using var scope = _application.Services.CreateScope();
            var processor = scope.ServiceProvider.GetRequiredService<DeviceMessageInboxProcessor>();
            return await processor.ProcessNextAsync(CancellationToken.None);
        }

        public async Task StopHostAsync()
        {
            if (Interlocked.Exchange(ref _stopped, 1) == 0)
                await _application.StopAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _stopped, 1) == 0)
            {
                try { await Manager.StopAsync(CancellationToken.None); }
                finally { await _application.StopAsync(); }
            }
            await _application.DisposeAsync();
        }
    }

    private static TelemetryNode CreateNode(string connectionString)
        => CreateNode(options => options.UseSqlite(connectionString));

    private static TelemetryNode CreatePostgresNode(string connectionString)
        => CreateNode(options => options.UseNpgsql(connectionString));

    private static TelemetryNode CreateNode(Action<DbContextOptionsBuilder> configureDatabase)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddDbContext<AppDbContext>(configureDatabase)
            .AddSingleton<DeviceMessageInboxAdmissionGate>()
            .AddScoped<DeviceMessageInboxService>()
            .AddScoped<IDeviceTelemetryMessageHandler, TestDeviceTelemetryMessageHandler>()
            .AddScoped<DeviceMessageInboxProcessor>()
            .AddSingleton(sp => new TelemetryPanelHub(NullLogger<TelemetryPanelHub>.Instance))
            .AddSingleton(sp => new TelemetryConnectionManager(
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<TelemetryPanelHub>(),
                NullLogger<TelemetryConnectionManager>.Instance))
            .BuildServiceProvider();
        return new TelemetryNode(services, services.GetRequiredService<TelemetryConnectionManager>());
    }

    private sealed class TestDeviceTelemetryMessageHandler : IDeviceTelemetryMessageHandler
    {
        public Task HandleDeviceMessageAsync(InboundDeviceMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class DelegateDeviceTelemetryMessageHandler(
        Func<InboundDeviceMessage, CancellationToken, Task> handler) : IDeviceTelemetryMessageHandler
    {
        public Task HandleDeviceMessageAsync(InboundDeviceMessage message, CancellationToken cancellationToken)
            => handler(message, cancellationToken);
    }

    private sealed class FakeProviderHttpClientFactory : IHttpClientFactory
    {
        private int _refundRequestCount;
        private readonly ConcurrentDictionary<string, int> _paymentRequestCounts = new(StringComparer.Ordinal);
        public int RefundRequestCount => Volatile.Read(ref _refundRequestCount);
        public int GetPaymentRequestCount(string paymentId) => _paymentRequestCounts.GetValueOrDefault(paymentId);

        public HttpClient CreateClient(string name) => new(new FakeProviderResponseHandler(
            paymentId => _paymentRequestCounts.AddOrUpdate(paymentId, 1, (_, count) => count + 1),
            () => Interlocked.Increment(ref _refundRequestCount)));
    }

    private sealed class FakeProviderResponseHandler(Func<string, int> countPaymentRequest, Action onRefundRequest) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("synthetic-access-token", request.Headers.Authorization?.Parameter);
            string responseBody;
            if (request.Method == HttpMethod.Get && request.RequestUri?.Host == "api.mercadopago.com" &&
                request.RequestUri.AbsolutePath.StartsWith("/v1/payments/", StringComparison.Ordinal))
            {
                var paymentId = request.RequestUri.Segments.Last();
                var attempt = countPaymentRequest(paymentId);
                if (paymentId == "123456792" && attempt == 1)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
                    {
                        Content = new StringContent("falha sintética temporária", Encoding.UTF8, "text/plain")
                    });
                }

                responseBody = paymentId == "123456790"
                    ? "{\"status\":\"rejected\",\"status_detail\":\"cc_rejected_bad_filled_other\",\"transaction_amount_refunded\":0}"
                    : "{\"status\":\"approved\",\"status_detail\":\"accredited\",\"transaction_amount_refunded\":0}";
            }
            else if (request.Method == HttpMethod.Post &&
                     request.RequestUri?.AbsolutePath.EndsWith("/refunds", StringComparison.Ordinal) == true)
            {
                onRefundRequest();
                responseBody = "{\"status\":\"approved\"}";
            }
            else
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "VendingMachines.Api.Tests";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static string StartupFrame(string? credential)
    {
        var credentialPart = credential == null ? string.Empty : $",\"credential\":\"{credential}\"";
        return $"{{\"id\":1,\"type\":\"start\",\"target\":\"SERIAL-1\"{credentialPart}}}";
    }

    private static async Task<TelemetryScenario> CreateScenarioAsync(
        bool requireCredentials,
        DateTime? revokedAt,
        IHostApplicationLifetime? lifetime = null,
        int? applicationStoppingDrainTimeoutSeconds = null)
    {
        var connectionString = $"Data Source=telemetry-handshake-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
        var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var company = new Company { Id = Guid.NewGuid(), Name = "Empresa de teste", ActivationStatus = CompanyActivationStatuses.Active };
        var credential = DeviceCredentialService.Generate();
        var machine = new Machine
        {
            Id = "machine-1", Name = "Máquina de teste", SerialNumber = "SERIAL-1",
            NormalizedSerialNumber = "SERIAL-1", CompanyId = company.Id, Company = company,
            TelemetryCredentialHash = DeviceCredentialService.Hash(credential),
            TelemetryCredentialRevokedAt = revokedAt
        };
        db.Companies.Add(company);
        db.Machines.Add(machine);
        await db.SaveChangesAsync();

        var services = new ServiceCollection()
            .AddDbContext<AppDbContext>(builder => builder.UseSqlite(connectionString))
            .BuildServiceProvider();
        var configurationValues = new Dictionary<string, string?>
        {
            ["Telemetry:RequireDeviceCredentials"] = requireCredentials.ToString()
        };
        if (applicationStoppingDrainTimeoutSeconds.HasValue)
            configurationValues["Telemetry:ApplicationStoppingDrainTimeoutSeconds"] = applicationStoppingDrainTimeoutSeconds.Value.ToString();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configurationValues)
            .Build();
        var manager = new TelemetryConnectionManager(
            services.GetRequiredService<IServiceScopeFactory>(),
            new TelemetryPanelHub(NullLogger<TelemetryPanelHub>.Instance),
            NullLogger<TelemetryConnectionManager>.Instance,
            configuration,
            lifetime);

        return new TelemetryScenario(connection, db, services, manager, credential);
    }

    private sealed class TelemetryScenario(
        SqliteConnection connection,
        AppDbContext db,
        ServiceProvider services,
        TelemetryConnectionManager manager,
        string credential) : IAsyncDisposable
    {
        public AppDbContext Db { get; } = db;
        public TelemetryConnectionManager Manager { get; } = manager;
        public string Credential { get; } = credential;

        public async ValueTask DisposeAsync()
        {
            Manager.Dispose();
            services.Dispose();
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class TelemetryNode(ServiceProvider services, TelemetryConnectionManager manager) : IAsyncDisposable
    {
        public TelemetryConnectionManager Manager { get; } = manager;
        public async ValueTask DisposeAsync()
        {
            try { await Manager.StopAsync(CancellationToken.None); }
            catch { }
            await services.DisposeAsync();
        }
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication() => _stopping.Cancel();
    }

    private sealed class ScriptedWebSocket : WebSocket
    {
        private readonly Channel<(byte[] Payload, WebSocketMessageType Type)> _incoming =
            Channel.CreateUnbounded<(byte[] Payload, WebSocketMessageType Type)>();
        private readonly object _sentLock = new();
        private readonly List<string> _sentMessages = [];
        private WebSocketState _state = WebSocketState.Open;
        private WebSocketCloseStatus? _closeStatus;
        private string? _closeStatusDescription;
        private readonly TaskCompletionSource _closeOutputRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _closeRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstTextSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CloseStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CloseCancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CloseOutputStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CloseOutputCancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool BlockClose { get; init; }
        public bool BlockCloseOutput { get; init; }
        public IReadOnlyList<string> SentMessages
        {
            get { lock (_sentLock) return _sentMessages.ToArray(); }
        }
        public bool WasAborted { get; private set; }
        public override WebSocketCloseStatus? CloseStatus => _closeStatus;
        public override string? CloseStatusDescription => _closeStatusDescription;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public void SendText(string message) => _incoming.Writer.TryWrite((Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text));
        public void SendClose() => _incoming.Writer.TryWrite((Array.Empty<byte>(), WebSocketMessageType.Close));
        public void ReleaseClose() => _closeRelease.TrySetResult();
        public void ReleaseCloseOutput() => _closeOutputRelease.TrySetResult();

        public override void Abort()
        {
            WasAborted = true;
            _state = WebSocketState.Aborted;
        }

        public override void Dispose() => _state = WebSocketState.Closed;

        public override async Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _closeStatus = closeStatus;
            _closeStatusDescription = statusDescription;
            if (BlockClose)
            {
                CloseStarted.TrySetResult();
                try
                {
                    await _closeRelease.Task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    CloseCancellationObserved.TrySetResult();
                    throw;
                }
            }

            _state = WebSocketState.Closed;
        }

        public override async Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            if (BlockCloseOutput)
            {
                CloseOutputStarted.TrySetResult();
                try
                {
                    await _closeOutputRelease.Task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    CloseOutputCancellationObserved.TrySetResult();
                    throw;
                }
            }

            _closeStatus = closeStatus;
            _closeStatusDescription = statusDescription;
            _state = WebSocketState.Closed;
            _incoming.Writer.TryWrite((Array.Empty<byte>(), WebSocketMessageType.Close));
        }

        public override async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var frame = await _incoming.Reader.ReadAsync(cancellationToken);
            if (frame.Type == WebSocketMessageType.Close)
            {
                _state = WebSocketState.CloseReceived;
                return new ValueWebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            }

            frame.Payload.AsSpan().CopyTo(buffer.Span);
            return new ValueWebSocketReceiveResult(frame.Payload.Length, frame.Type, true);
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            var frame = await _incoming.Reader.ReadAsync(cancellationToken);
            if (frame.Type == WebSocketMessageType.Close)
            {
                _state = WebSocketState.CloseReceived;
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, CloseStatus, CloseStatusDescription);
            }

            frame.Payload.AsSpan().CopyTo(buffer.AsSpan());
            return new WebSocketReceiveResult(frame.Payload.Length, frame.Type, true);
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            RecordSent(buffer.AsSpan(), messageType);
            return Task.CompletedTask;
        }

        public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            RecordSent(buffer.Span, messageType);
            return ValueTask.CompletedTask;
        }

        private void RecordSent(ReadOnlySpan<byte> buffer, WebSocketMessageType messageType)
        {
            if (messageType != WebSocketMessageType.Text) return;
            lock (_sentLock) _sentMessages.Add(Encoding.UTF8.GetString(buffer));
            FirstTextSent.TrySetResult();
        }
    }
}
