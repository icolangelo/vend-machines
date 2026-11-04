using Npgsql;
using VendingMachines.Api.Data;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class DatabasePoolConfigurationTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(1000)]
    public void AppliesOneMaximumToTheCandidateConnectionString(int maximumPoolSize)
    {
        var configured = DatabasePoolConfiguration.ApplyMaximumPoolSize(
            "Host=db.production.internal;Database=machines;Username=service;Password=synthetic;SSL Mode=Require",
            maximumPoolSize);

        Assert.Equal(maximumPoolSize, new NpgsqlConnectionStringBuilder(configured).MaxPoolSize);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1001)]
    public void RejectsMaximumOutsideSupportedRange(int maximumPoolSize)
    {
        Assert.Throws<InvalidOperationException>(() =>
            DatabasePoolConfiguration.ApplyMaximumPoolSize(
                "Host=db.production.internal;Database=machines;Username=service;Password=synthetic;SSL Mode=Require",
                maximumPoolSize));
    }

    [Fact]
    public void RejectsMissingConnectionString()
    {
        Assert.Throws<InvalidOperationException>(() =>
            DatabasePoolConfiguration.ApplyMaximumPoolSize(null, 5));
    }

    [Fact]
    public void RejectsDisabledPooling()
    {
        Assert.Throws<InvalidOperationException>(() =>
            DatabasePoolConfiguration.ApplyMaximumPoolSize(
                "Host=db.production.internal;Database=machines;Username=service;Password=synthetic;SSL Mode=Require;Pooling=false",
                5));
    }
}
