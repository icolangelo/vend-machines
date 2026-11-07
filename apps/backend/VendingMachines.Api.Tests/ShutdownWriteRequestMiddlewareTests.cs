using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using VendingMachines.Api.Services;
using VendingMachines.Api.Middlewares;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class ShutdownWriteRequestMiddlewareTests
{
    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task RejectsNewWriteRequestsAfterShutdownStarts(string method)
    {
        using var lifetime = new TestApplicationLifetime();
        var writeRequestGate = new ShutdownWriteRequestGate();
        lifetime.BeginStopping();
        var nextCalled = false;
        var middleware = new ShutdownWriteRequestMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, lifetime, writeRequestGate);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Request.Method = method;

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("5", context.Response.Headers.RetryAfter);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        var body = await reader.ReadToEndAsync();
        Assert.Contains("server_shutting_down", body, StringComparison.Ordinal);
        Assert.Contains("O servidor está sendo atualizado", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsWriteAfterAdmissionClosesEvenBeforeLifetimeTokenChanges()
    {
        using var lifetime = new TestApplicationLifetime();
        var writeRequestGate = new ShutdownWriteRequestGate();
        writeRequestGate.StopAccepting();
        var nextCalled = false;
        var middleware = new ShutdownWriteRequestMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, lifetime, writeRequestGate);
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Request.Method = "POST";

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("5", context.Response.Headers.RetryAfter);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task AllowsReadAndPreflightRequestsDuringShutdown(string method)
    {
        using var lifetime = new TestApplicationLifetime();
        var writeRequestGate = new ShutdownWriteRequestGate();
        lifetime.BeginStopping();
        var nextCalled = false;
        var middleware = new ShutdownWriteRequestMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, lifetime, writeRequestGate);
        var context = new DefaultHttpContext();
        context.Request.Method = method;

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task AllowsWritesBeforeShutdownStarts()
    {
        using var lifetime = new TestApplicationLifetime();
        var writeRequestGate = new ShutdownWriteRequestGate();
        var nextCalled = false;
        var middleware = new ShutdownWriteRequestMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, lifetime, writeRequestGate);
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task HoldsWriteAdmissionUntilControllerRequestFinishes()
    {
        using var lifetime = new TestApplicationLifetime();
        var writeRequestGate = new ShutdownWriteRequestGate();
        var requestEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var middleware = new ShutdownWriteRequestMiddleware(async _ =>
        {
            requestEntered.TrySetResult();
            await finishRequest.Task;
        }, lifetime, writeRequestGate);
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";

        var request = middleware.InvokeAsync(context);
        await requestEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        writeRequestGate.StopAccepting();
        var drained = writeRequestGate.WaitForDrainAsync(CancellationToken.None);

        Assert.False(drained.IsCompleted);
        Assert.Equal(1, writeRequestGate.ActiveRequestCount);
        finishRequest.TrySetResult();
        await request.WaitAsync(TimeSpan.FromSeconds(2));
        await drained.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, writeRequestGate.ActiveRequestCount);
    }

    private sealed class TestApplicationLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => BeginStopping();
        public void BeginStopping() => _stopping.Cancel();
        public void Dispose() => _stopping.Dispose();
    }
}
