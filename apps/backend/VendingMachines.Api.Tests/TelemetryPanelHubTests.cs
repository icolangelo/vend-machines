using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class TelemetryPanelHubTests
{
    [Fact]
    public async Task ClientCloseDoesNotKeepPanelConnectionWhenPeerDoesNotFinishHandshake()
    {
        var hub = new TelemetryPanelHub(NullLogger<TelemetryPanelHub>.Instance);
        using var socket = new BlockingCloseWebSocket();
        var ticket = new WebSocketTicket(Guid.NewGuid().ToString("N"), Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow.AddMinutes(1));

        var handling = hub.HandleAsync(socket, ticket, CancellationToken.None);
        await socket.FirstTextSent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        socket.SendClose();
        await socket.CloseStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        try
        {
            await socket.CloseCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(4));
            await handling.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(socket.WasAborted);
        }
        finally
        {
            socket.ReleaseClose();
            await handling.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task BlockedPanelWriterIsAbortedWhenRequestIsCancelled()
    {
        var hub = new TelemetryPanelHub(NullLogger<TelemetryPanelHub>.Instance);
        using var socket = new BlockingCloseWebSocket { BlockFirstSend = true };
        using var requestAborted = new CancellationTokenSource();
        var ticket = new WebSocketTicket(Guid.NewGuid().ToString("N"), Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow.AddMinutes(1));

        var handling = hub.HandleAsync(socket, ticket, requestAborted.Token);
        await socket.FirstTextSent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        requestAborted.Cancel();

        await handling.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(socket.WasAborted);
    }

    [Fact]
    public async Task TransportFailureEndsPanelHandlerWithoutEscaping()
    {
        var hub = new TelemetryPanelHub(NullLogger<TelemetryPanelHub>.Instance);
        using var socket = new BlockingCloseWebSocket { ThrowOnReceive = true, BlockClose = false };
        var ticket = new WebSocketTicket(Guid.NewGuid().ToString("N"), Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow.AddMinutes(1));

        var handling = hub.HandleAsync(socket, ticket, CancellationToken.None);
        await socket.FirstTextSent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await handling.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(WebSocketState.Closed, socket.State);
    }

    private sealed class BlockingCloseWebSocket : WebSocket
    {
        private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
        private readonly TaskCompletionSource _releaseClose = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseSend = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _sendBlocked;
        private WebSocketState _state = WebSocketState.Open;

        public TaskCompletionSource FirstTextSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CloseStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CloseCancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool BlockFirstSend { get; init; }
        public bool BlockClose { get; init; } = true;
        public bool ThrowOnReceive { get; init; }
        public bool WasAborted { get; private set; }
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public void SendClose() => _incoming.Writer.TryWrite([]);
        public void ReleaseClose() => _releaseClose.TrySetResult();

        public override void Abort()
        {
            WasAborted = true;
            _state = WebSocketState.Aborted;
            _releaseSend.TrySetResult();
        }

        public override void Dispose() => _state = WebSocketState.Closed;

        public override async Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            if (!BlockClose)
            {
                _state = WebSocketState.Closed;
                return;
            }

            CloseStarted.TrySetResult();
            try
            {
                await _releaseClose.Task.WaitAsync(cancellationToken);
                _state = WebSocketState.Closed;
            }
            catch (OperationCanceledException)
            {
                CloseCancellationObserved.TrySetResult();
                throw;
            }
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public override async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken)
        {
            if (ThrowOnReceive) throw new WebSocketException("Falha de transporte simulada.");
            await _incoming.Reader.ReadAsync(cancellationToken);
            _state = WebSocketState.CloseReceived;
            return new ValueWebSocketReceiveResult(0, WebSocketMessageType.Close, true);
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            if (ThrowOnReceive) throw new WebSocketException("Falha de transporte simulada.");
            await _incoming.Reader.ReadAsync(cancellationToken);
            _state = WebSocketState.CloseReceived;
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, CloseStatus, CloseStatusDescription);
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            return SendTextAsync(messageType);
        }

        public override ValueTask SendAsync(
            ReadOnlyMemory<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            return new ValueTask(SendTextAsync(messageType));
        }

        private async Task SendTextAsync(WebSocketMessageType messageType)
        {
            if (messageType != WebSocketMessageType.Text) return;
            FirstTextSent.TrySetResult();
            if (BlockFirstSend && Interlocked.Exchange(ref _sendBlocked, 1) == 0)
                await _releaseSend.Task;
        }
    }
}
