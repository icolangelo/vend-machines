using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class CorsOriginConfigurationTests
{
    [Fact]
    public void ProductionAcceptsHttpsOriginsAndNormalizesTrailingSlash()
    {
        var origins = CorsOriginConfiguration.Validate(
            ["https://app.vendmachine.com.br/", "https://app.vendmachine.com.br"],
            requireHttps: true);

        Assert.Equal(new[] { "https://app.vendmachine.com.br" }, origins);
    }

    [Theory]
    [InlineData("http://app.vendmachine.com.br")]
    [InlineData("https://localhost:5173")]
    [InlineData("https://api.example.com")]
    [InlineData("*")]
    [InlineData("https://app.vendmachine.com.br/path")]
    [InlineData("https://user@app.vendmachine.com.br")]
    public void ProductionRejectsInsecureOrInvalidOrigins(string origin)
    {
        Assert.Throws<InvalidOperationException>(() =>
            CorsOriginConfiguration.Validate([origin], requireHttps: true));
    }

    [Fact]
    public void DevelopmentAllowsHttpLoopback()
    {
        var origins = CorsOriginConfiguration.Validate(
            ["http://localhost:5173"],
            requireHttps: false);

        Assert.Equal(new[] { "http://localhost:5173" }, origins);
    }

    [Fact]
    public void MissingOriginsAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() =>
            CorsOriginConfiguration.Validate([], requireHttps: true));
    }
}
