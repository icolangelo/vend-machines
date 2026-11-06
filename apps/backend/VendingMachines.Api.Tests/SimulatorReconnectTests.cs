using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using VendingMachines.Simulator;
using Xunit;

namespace VendingMachines.Api.Tests;

[Collection("Telemetry WebSocket resource tests")]
public sealed class SimulatorReconnectTests
{
    [Theory]
    [InlineData(1, 0, 0.8)]
    [InlineData(1, 0.5, 1)]
    [InlineData(1, 1, 1.2)]
    [InlineData(6, 0, 24)]
    [InlineData(6, 1, 30)]
    [InlineData(20, 0.5, 30)]
    public void ReconnectDelayUsesCappedExponentialBackoffWithJitter(int attempt, double jitterSample, double expectedSeconds)
    {
        var delay = MachineSimulator.CalculateReconnectDelay(attempt, jitterSample);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(29)]
    public void ShortConnectionsDoNotResetReconnectBackoff(int seconds) =>
        Assert.False(MachineSimulator.ShouldResetReconnectAttempt(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(30)]
    [InlineData(300)]
    public void StableConnectionsResetReconnectBackoff(int seconds) =>
        Assert.True(MachineSimulator.ShouldResetReconnectAttempt(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void ReconnectDelayRejectsInvalidInputs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MachineSimulator.CalculateReconnectDelay(0, 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => MachineSimulator.CalculateReconnectDelay(1, -0.1));
        Assert.Throws<ArgumentOutOfRangeException>(() => MachineSimulator.CalculateReconnectDelay(1, double.NaN));
    }

    [Fact]
    public async Task SimulatorReconnectsAfterGracefulServerShutdown()
    {
        await using var server = await RunningServer.StartAsync();
        using var shutdown = new CancellationTokenSource();
        var simulator = new MachineSimulator(
            new CliOptions("TEST-RECONNECT", server.WebSocketUri, 20, true, TimeSpan.FromHours(1), true),
            new SimulatorLog());

        var simulatorTask = simulator.RunAsync(shutdown.Token);
        try
        {
            await server.FirstConnection.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForStateAsync(simulator, SimulatorState.Connected, TimeSpan.FromSeconds(5));

            server.RequestGracefulShutdown();
            await server.SecondConnection.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForStateAsync(simulator, SimulatorState.Connected, TimeSpan.FromSeconds(5));

            Assert.Equal(2, server.ConnectionCount);
            Assert.True(simulator.IsConnected);
        }
        finally
        {
            shutdown.Cancel();
            await simulatorTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task FleetOfSeventyEightSimulatorsReconnectsAfterGracefulServerShutdown()
    {
        const int fleetSize = 78;
        await using var server = await FleetRunningServer.StartAsync(fleetSize);
        using var shutdown = new CancellationTokenSource();
        var simulators = Enumerable.Range(1, fleetSize)
            .Select(index => new MachineSimulator(
                new CliOptions($"FLEET-{index:D3}", server.WebSocketUri, 20, true, TimeSpan.FromHours(1), true),
                new SimulatorLog(writeToConsole: false)))
            .ToArray();
        var simulatorTasks = simulators.Select(simulator => simulator.RunAsync(shutdown.Token)).ToArray();

        try
        {
            await server.InitialConnections.WaitAsync(TimeSpan.FromSeconds(20));
            await WaitForFleetStateAsync(simulators, SimulatorState.Connected, TimeSpan.FromSeconds(10));

            server.RequestGracefulShutdown();
            await server.Reconnections.WaitAsync(TimeSpan.FromSeconds(20));
            await WaitForFleetStateAsync(simulators, SimulatorState.Connected, TimeSpan.FromSeconds(10));

            Assert.Equal(fleetSize, server.InitialConnectionCount);
            Assert.Equal(fleetSize, server.ReconnectionCount);
            Assert.All(simulators, simulator => Assert.True(simulator.IsConnected, simulator.Serial));
            Assert.All(simulators, simulator => Assert.Equal(2, server.GetConnectionAttempts(simulator.Serial)));
        }
        finally
        {
            shutdown.Cancel();
            await Task.WhenAll(simulatorTasks).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static async Task WaitForStateAsync(
        MachineSimulator simulator,
        SimulatorState expected,
        TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (simulator.State != expected)
            await Task.Delay(25, cancellation.Token);
    }

    private static async Task WaitForFleetStateAsync(
        IReadOnlyCollection<MachineSimulator> simulators,
        SimulatorState expected,
        TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (simulators.Any(simulator => simulator.State != expected))
            await Task.Delay(25, cancellation.Token);
    }

    private sealed class FleetRunningServer : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly int _fleetSize;
        private readonly TaskCompletionSource _gracefulShutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _initialConnections = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _reconnections = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<string, int> _connectionAttempts = new(StringComparer.OrdinalIgnoreCase);
        private int _initialConnectionCount;
        private int _reconnectionCount;

        private FleetRunningServer(WebApplication app, int fleetSize)
        {
            _app = app;
            _fleetSize = fleetSize;
        }

        public Uri WebSocketUri { get; private set; } = null!;
        public Task InitialConnections => _initialConnections.Task;
        public Task Reconnections => _reconnections.Task;
        public int InitialConnectionCount => Volatile.Read(ref _initialConnectionCount);
        public int ReconnectionCount => Volatile.Read(ref _reconnectionCount);
        public int GetConnectionAttempts(string serial) => _connectionAttempts.TryGetValue(serial, out var count) ? count : 0;
        public void RequestGracefulShutdown() => _gracefulShutdown.TrySetResult();

        public static async Task<FleetRunningServer> StartAsync(int fleetSize)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();
            var server = new FleetRunningServer(app, fleetSize);

            app.UseWebSockets();
            app.Map("/telemetria", async context =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                var startFrame = await ReceiveTextAsync(socket, context.RequestAborted);
                using var startDocument = JsonDocument.Parse(startFrame);
                var root = startDocument.RootElement;
                var serial = root.GetProperty("target").GetString();
                var messageId = root.GetProperty("id").GetInt32();
                if (string.IsNullOrWhiteSpace(serial))
                {
                    await socket.CloseAsync(WebSocketCloseStatus.ProtocolError, "Serial ausente", context.RequestAborted);
                    return;
                }

                var attempt = server._connectionAttempts.AddOrUpdate(serial, 1, (_, previous) => previous + 1);
                await SendTextAsync(socket, JsonSerializer.Serialize(new { type = "ack", id = messageId }), context.RequestAborted);

                if (attempt == 1)
                {
                    if (Interlocked.Increment(ref server._initialConnectionCount) == server._fleetSize)
                        server._initialConnections.TrySetResult();

                    await server._gracefulShutdown.Task.WaitAsync(context.RequestAborted);
                    await SendTextAsync(socket, "{\"type\":\"EndpointUnavailable\"}", context.RequestAborted);
                    await socket.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "Versão em substituição", context.RequestAborted);
                    return;
                }

                if (attempt != 2)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Tentativa duplicada inesperada", context.RequestAborted);
                    return;
                }

                if (Interlocked.Increment(ref server._reconnectionCount) == server._fleetSize)
                    server._reconnections.TrySetResult();

                var buffer = new byte[1024];
                while (socket.State == WebSocketState.Open)
                {
                    var received = await socket.ReceiveAsync(buffer, context.RequestAborted);
                    if (received.MessageType != WebSocketMessageType.Close) continue;
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Fechamento confirmado", CancellationToken.None);
                    return;
                }
            });

            await app.StartAsync();
            var addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses;
            var httpUri = new Uri(addresses.Single());
            server.WebSocketUri = new UriBuilder(httpUri) { Scheme = "ws", Path = "/telemetria" }.Uri;
            return server;
        }

        public async ValueTask DisposeAsync()
        {
            _gracefulShutdown.TrySetCanceled();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await _app.StopAsync(timeout.Token); }
            catch (OperationCanceledException) { }
            await _app.DisposeAsync();
        }
    }

    private sealed class RunningServer : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly TaskCompletionSource _gracefulShutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstConnection = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondConnection = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _connectionCount;

        private RunningServer(WebApplication app) => _app = app;

        public Uri WebSocketUri { get; private set; } = null!;
        public Task FirstConnection => _firstConnection.Task;
        public Task SecondConnection => _secondConnection.Task;
        public int ConnectionCount => Volatile.Read(ref _connectionCount);

        public void RequestGracefulShutdown() => _gracefulShutdown.TrySetResult();

        public static async Task<RunningServer> StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();
            var server = new RunningServer(app);

            app.UseWebSockets();
            app.Map("/telemetria", async context =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                var connectionNumber = Interlocked.Increment(ref server._connectionCount);
                var startFrame = await ReceiveTextAsync(socket, context.RequestAborted);
                using var startDocument = JsonDocument.Parse(startFrame);
                var messageId = startDocument.RootElement.GetProperty("id").GetInt32();
                await SendTextAsync(socket, JsonSerializer.Serialize(new { type = "ack", id = messageId }), context.RequestAborted);

                if (connectionNumber == 1)
                {
                    server._firstConnection.TrySetResult();
                    await server._gracefulShutdown.Task.WaitAsync(context.RequestAborted);
                    await SendTextAsync(socket, "{\"type\":\"EndpointUnavailable\"}", context.RequestAborted);
                    await socket.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "Versão em substituição", context.RequestAborted);
                    return;
                }

                server._secondConnection.TrySetResult();
                var buffer = new byte[1024];
                while (socket.State == WebSocketState.Open)
                {
                    var received = await socket.ReceiveAsync(buffer, context.RequestAborted);
                    if (received.MessageType != WebSocketMessageType.Close) continue;
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Fechamento confirmado", CancellationToken.None);
                    return;
                }
            });

            await app.StartAsync();
            var addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses;
            var httpUri = new Uri(addresses.Single());
            server.WebSocketUri = new UriBuilder(httpUri) { Scheme = "ws", Path = "/telemetria" }.Uri;
            return server;
        }

        public async ValueTask DisposeAsync()
        {
            _gracefulShutdown.TrySetCanceled();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await _app.StopAsync(timeout.Token); }
            catch (OperationCanceledException) { }
            await _app.DisposeAsync();
        }
    }

    private static async Task<string> ReceiveTextAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException("O simulador fechou antes de enviar o handshake.");
            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return Encoding.UTF8.GetString(stream.ToArray());
        }
    }

    private static Task SendTextAsync(WebSocket socket, string text, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }
}
