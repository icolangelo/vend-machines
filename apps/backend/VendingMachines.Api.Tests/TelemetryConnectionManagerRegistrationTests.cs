using System.Net.WebSockets;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class TelemetryConnectionManagerRegistrationTests
{
    [Fact]
    public void ConnectionOperationContractsResolveToTheSameSingletonManager()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TelemetryPanelHub>();
        services.AddTelemetryConnectionOperations();

        using var provider = services.BuildServiceProvider();
        var manager = provider.GetRequiredService<TelemetryConnectionManager>();

        Assert.Same(manager, provider.GetRequiredService<ITelemetryCommandSender>());
        Assert.Same(manager, provider.GetRequiredService<ITelemetryConnectionOperations>());
    }

    [Fact]
    public void LegacyDuplicateSerialIsRejectedWithoutDisconnectingTheActiveMachine()
    {
        using var manager = CreateManager(requireCredentials: false);
        var (register, connectionType, constructor) = GetRegistrationMembers();
        var previousSocket = new FakeWebSocket();
        var duplicateSocket = new FakeWebSocket();
        var companyId = Guid.NewGuid();
        var previous = constructor.Invoke(new object[] { "machine-1", companyId, "SERIAL-1", previousSocket, false });
        var duplicate = constructor.Invoke(new object[] { "machine-1", companyId, "SERIAL-1", duplicateSocket, false });

        Assert.True((bool)register!.Invoke(manager, new[] { previous })!);
        Assert.False((bool)register.Invoke(manager, new[] { duplicate })!);

        var previousCancellation = (CancellationTokenSource)connectionType.GetProperty("Cts")!.GetValue(previous)!;
        var duplicateCancellation = (CancellationTokenSource)connectionType.GetProperty("Cts")!.GetValue(duplicate)!;
        Assert.False(previousSocket.WasAborted);
        Assert.False(previousCancellation.IsCancellationRequested);
        Assert.False(duplicateCancellation.IsCancellationRequested);
        Assert.Same(previous, GetRegisteredConnection(manager, connectionType, "SERIAL-1"));
        Assert.True(manager.IsOnline(" SERIAL-1 "));
        var connectionId = (string)connectionType.GetProperty("ConnectionId")!.GetValue(previous)!;
        Assert.Equal(connectionId, manager.GetActiveMachineConnectionsForLease()["machine-1"]);

        previousSocket.Dispose();
        Assert.Empty(manager.GetActiveMachineConnectionsForLease());
    }

    [Fact]
    public void AuthenticatedDuplicateSerialReplacesPreviousConnection()
    {
        using var manager = CreateManager(requireCredentials: true);
        var (register, connectionType, constructor) = GetRegistrationMembers();
        var previousSocket = new FakeWebSocket();
        var currentSocket = new FakeWebSocket();
        var companyId = Guid.NewGuid();
        var previous = constructor.Invoke(new object[] { "machine-1", companyId, "SERIAL-1", previousSocket, true });
        var current = constructor.Invoke(new object[] { "machine-1", companyId, "SERIAL-1", currentSocket, true });

        Assert.True((bool)register!.Invoke(manager, new[] { previous })!);
        Assert.True((bool)register.Invoke(manager, new[] { current })!);

        Assert.True(previousSocket.WasAborted);
        var previousCancellation = (CancellationTokenSource)connectionType.GetProperty("Cts")!.GetValue(previous)!;
        Assert.True(previousCancellation.IsCancellationRequested);
        Assert.Same(current, GetRegisteredConnection(manager, connectionType, "SERIAL-1"));
        Assert.True(manager.IsOnline("SERIAL-1"));
        var currentConnectionId = (string)connectionType.GetProperty("ConnectionId")!.GetValue(current)!;
        Assert.Equal(currentConnectionId, manager.GetActiveMachineConnectionsForLease()["machine-1"]);
    }

    [Fact]
    public void LeaseSnapshotOmitsMachineWithAmbiguousLocalSockets()
    {
        using var manager = CreateManager(requireCredentials: true);
        var (register, _, constructor) = GetRegistrationMembers();
        var companyId = Guid.NewGuid();
        var first = constructor.Invoke(new object[] { "machine-1", companyId, "SERIAL-1", new FakeWebSocket(), true });
        var second = constructor.Invoke(new object[] { "machine-1", companyId, "SERIAL-2", new FakeWebSocket(), true });

        Assert.True((bool)register!.Invoke(manager, new[] { first })!);
        Assert.True((bool)register.Invoke(manager, new[] { second })!);

        Assert.Empty(manager.GetActiveMachineConnectionsForLease());
    }

    [Fact]
    public async Task ShutdownRejectsNewSessionStartsAndWaitsForAdmittedStartsToFinish()
    {
        using var lifetime = new ApiReadinessEndpointTests.TestApplicationLifetime();
        using var manager = CreateManager(requireCredentials: false, lifetime);
        using var admittedStart = manager.TryBeginSessionStart();
        Assert.NotNull(admittedStart);

        var stopping = Task.Run(lifetime.BeginStopping);
        IDisposable? probe = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            probe = manager.TryBeginSessionStart();
            if (probe == null) break;
            probe.Dispose();
            await Task.Delay(10);
        }

        Assert.Null(probe);
        Assert.False(stopping.IsCompleted);

        admittedStart.Dispose();
        await stopping.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Null(manager.TryBeginSessionStart());
    }

    [Fact]
    public async Task ShutdownWaitsForAdmittedWriteRequestsBeforeDrainingTelemetry()
    {
        using var lifetime = new ApiReadinessEndpointTests.TestApplicationLifetime();
        var writeRequestGate = new ShutdownWriteRequestGate();
        using var manager = CreateManager(requireCredentials: false, lifetime, writeRequestGate);
        using var admittedWrite = writeRequestGate.TryBeginWriteRequest();
        Assert.NotNull(admittedWrite);

        var stopping = Task.Run(lifetime.BeginStopping);
        IDisposable? probe = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            probe = writeRequestGate.TryBeginWriteRequest();
            if (probe == null) break;
            probe.Dispose();
            await Task.Delay(10);
        }

        Assert.Null(probe);
        Assert.False(stopping.IsCompleted);

        admittedWrite.Dispose();
        await stopping.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static TelemetryConnectionManager CreateManager(
        bool requireCredentials,
        IHostApplicationLifetime? lifetime = null,
        ShutdownWriteRequestGate? shutdownWriteRequestGate = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telemetry:RequireDeviceCredentials"] = requireCredentials.ToString()
            })
            .Build();
        return new TelemetryConnectionManager(
            null!, null!, NullLogger<TelemetryConnectionManager>.Instance, configuration, lifetime,
            shutdownWriteRequestGate: shutdownWriteRequestGate);
    }

    private static (MethodInfo Register, Type ConnectionType, ConstructorInfo Constructor) GetRegistrationMembers()
    {
        var register = typeof(TelemetryConnectionManager).GetMethod(
            "TryRegisterDevice", BindingFlags.Instance | BindingFlags.NonPublic);
        var connectionType = typeof(TelemetryConnectionManager).GetNestedType(
            "DeviceConnection", BindingFlags.NonPublic);
        Assert.NotNull(register);
        Assert.NotNull(connectionType);
        var constructor = connectionType!.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single();
        return (register!, connectionType, constructor);
    }

    private static object GetRegisteredConnection(
        TelemetryConnectionManager manager,
        Type connectionType,
        string serial)
    {
        var devices = typeof(TelemetryConnectionManager).GetField("_devices", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(manager)!;
        var indexer = devices.GetType().GetProperty("Item", new[] { typeof(string) })!;
        var connection = indexer.GetValue(devices, new object[] { serial });
        Assert.NotNull(connection);
        Assert.IsAssignableFrom(connectionType, connection);
        return connection!;
    }

    private sealed class FakeWebSocket : WebSocket
    {
        private WebSocketState _state = WebSocketState.Open;

        public bool WasAborted { get; private set; }
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public override void Abort()
        {
            WasAborted = true;
            _state = WebSocketState.Aborted;
        }

        public override void Dispose() => _state = WebSocketState.Closed;
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
