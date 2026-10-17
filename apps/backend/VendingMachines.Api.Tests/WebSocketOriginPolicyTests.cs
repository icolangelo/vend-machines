using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class WebSocketOriginPolicyTests
{
    [Fact]
    public void UsesCorsOriginsWhenWebSocketOriginsAreNotConfigured()
    {
        var origins = WebSocketOriginPolicy.ResolveOrigins(null, ["https://app.example.com"]);
        var policy = new WebSocketOriginPolicy(origins);

        Assert.True(policy.Allows("https://app.example.com"));
        Assert.False(policy.Allows("https://attacker.example"));
        Assert.False(policy.Allows(null));
    }

    [Fact]
    public void ExplicitWebSocketOriginsReplaceCorsOrigins()
    {
        var origins = WebSocketOriginPolicy.ResolveOrigins(
            ["https://panel.example.com"],
            ["https://app.example.com"]);
        var policy = new WebSocketOriginPolicy(origins);

        Assert.True(policy.Allows("https://panel.example.com"));
        Assert.False(policy.Allows("https://app.example.com"));
    }

    [Theory]
    [InlineData("https://app.example.com/path")]
    [InlineData("https://user@app.example.com")]
    [InlineData("null")]
    [InlineData("*")]
    public void RejectsOriginsThatAreNotValidWebOrigins(string origin)
    {
        var policy = new WebSocketOriginPolicy(["https://app.example.com"]);

        Assert.False(policy.Allows(origin));
    }

    [Fact]
    public void FailsWhenNoUsableOriginsAreConfigured()
    {
        Assert.Throws<InvalidOperationException>(() =>
            WebSocketOriginPolicy.ResolveOrigins(["*", "not-an-origin"], []));
    }
}
