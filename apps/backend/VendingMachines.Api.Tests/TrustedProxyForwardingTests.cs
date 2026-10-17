using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class TrustedProxyForwardingTests
{
    [Fact]
    public void ForwardedHeadersRemainDisabledUnlessExplicitlyEnabled()
    {
        Assert.False(TrustedProxyForwarding.IsEnabled(CreateConfiguration()));
        Assert.True(TrustedProxyForwarding.IsEnabled(CreateConfiguration(("ForwardedHeaders:Enabled", "true"))));
    }

    [Fact]
    public async Task MiddlewareUsesForwardedValuesOnlyFromConfiguredProxy()
    {
        var configuration = CreateConfiguration(
            ("ForwardedHeaders:KnownProxies:0", "10.0.0.10"));
        var options = TrustedProxyForwarding.CreateOptions(configuration);
        using var loggerFactory = LoggerFactory.Create(_ => { });

        var trustedContext = CreateContext("10.0.0.10");
        trustedContext.Request.Headers["X-Forwarded-For"] = "198.51.100.25";
        trustedContext.Request.Headers["X-Forwarded-Proto"] = "https";
        var middleware = new ForwardedHeadersMiddleware(_ => Task.CompletedTask, loggerFactory, Options.Create(options));

        await middleware.Invoke(trustedContext);

        Assert.Equal(IPAddress.Parse("198.51.100.25"), trustedContext.Connection.RemoteIpAddress);
        Assert.Equal("https", trustedContext.Request.Scheme);

        var mappedTrustedContext = CreateContext("::ffff:10.0.0.10");
        mappedTrustedContext.Request.Headers["X-Forwarded-For"] = "198.51.100.26";
        mappedTrustedContext.Request.Headers["X-Forwarded-Proto"] = "https";

        await middleware.Invoke(mappedTrustedContext);

        Assert.Equal(IPAddress.Parse("198.51.100.26"), mappedTrustedContext.Connection.RemoteIpAddress);

        var untrustedContext = CreateContext("10.0.0.11");
        untrustedContext.Request.Headers["X-Forwarded-For"] = "203.0.113.40";
        untrustedContext.Request.Headers["X-Forwarded-Proto"] = "https";

        await middleware.Invoke(untrustedContext);

        Assert.Equal(IPAddress.Parse("10.0.0.11"), untrustedContext.Connection.RemoteIpAddress);
        Assert.Equal("http", untrustedContext.Request.Scheme);
    }

    [Fact]
    public void EnabledForwardingRequiresAnExplicitTrustedProxy()
    {
        var configuration = CreateConfiguration();

        var exception = Assert.Throws<InvalidOperationException>(
            () => TrustedProxyForwarding.CreateOptions(configuration));

        Assert.Contains("KnownProxies", exception.Message);
    }

    [Theory]
    [InlineData("proxy.internal")]
    [InlineData("not-an-ip")]
    public void RejectsProxyHostnamesAndInvalidAddresses(string address)
    {
        var configuration = CreateConfiguration(("ForwardedHeaders:KnownProxies:0", address));

        Assert.Throws<InvalidOperationException>(() => TrustedProxyForwarding.CreateOptions(configuration));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("6")]
    public void RejectsUnsafeForwardLimits(string limit)
    {
        var configuration = CreateConfiguration(
            ("ForwardedHeaders:KnownProxies:0", "10.0.0.10"),
            ("ForwardedHeaders:ForwardLimit", limit));

        Assert.Throws<InvalidOperationException>(() => TrustedProxyForwarding.CreateOptions(configuration));
    }

    private static DefaultHttpContext CreateContext(string remoteAddress)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteAddress);
        context.Request.Scheme = "http";
        return context;
    }

    private static IConfiguration CreateConfiguration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(value => value.Key, value => (string?)value.Value))
            .Build();
}
