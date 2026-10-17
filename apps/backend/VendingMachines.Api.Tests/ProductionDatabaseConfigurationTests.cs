using VendingMachines.Api.Data;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class ProductionDatabaseConfigurationTests
{
    [Fact]
    public void RejectsSqliteAndExplicitSqliteSwitch()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ProductionDatabaseConfiguration.Validate("Data Source=/tmp/vending.db", false, true));
        Assert.Throws<InvalidOperationException>(() =>
            ProductionDatabaseConfiguration.Validate(ValidConnection("Require"), true, false));
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("db.localhost")]
    public void RejectsLoopbackDatabaseHosts(string host)
    {
        var connection = ValidConnection("Require").Replace("db.production.internal", host, StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() =>
            ProductionDatabaseConfiguration.Validate(connection, false, false));
    }

    [Theory]
    [InlineData("Host=YOUR_NEON_HOST;Database=machines;Username=service;Password=synthetic;SSL Mode=Require")]
    [InlineData("Host=db.production.internal;Database=example;Username=service;Password=synthetic;SSL Mode=Require")]
    [InlineData("Host=db.production.internal;Database=machines;Username=YOUR_USERNAME;Password=synthetic;SSL Mode=Require")]
    public void RejectsPlaceholderConnectionParts(string connection)
    {
        Assert.Throws<InvalidOperationException>(() =>
            ProductionDatabaseConfiguration.Validate(connection, false, false));
    }

    [Theory]
    [InlineData("Prefer")]
    [InlineData("Allow")]
    [InlineData("Disable")]
    public void RejectsPostgresModesWithoutRequiredTls(string sslMode)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProductionDatabaseConfiguration.Validate(ValidConnection(sslMode), false, false));

        Assert.Contains("SSL Mode=Require, VerifyCA ou VerifyFull", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Require")]
    [InlineData("VerifyCA")]
    [InlineData("VerifyFull")]
    public void AcceptsExplicitPostgresTlsModes(string sslMode)
    {
        ProductionDatabaseConfiguration.Validate(ValidConnection(sslMode), false, false);
    }

    [Theory]
    [InlineData("Host=db.production.internal;Username=service;Password=synthetic;SSL Mode=Require")]
    [InlineData("Host=db.production.internal;Database=machines;Password=synthetic;SSL Mode=Require")]
    public void RequiresDatabaseAndUsername(string connection)
    {
        Assert.Throws<InvalidOperationException>(() =>
            ProductionDatabaseConfiguration.Validate(connection, false, false));
    }

    private static string ValidConnection(string sslMode) =>
        $"Host=db.production.internal;Database=machines;Username=service;Password=synthetic;SSL Mode={sslMode}";
}
