using VendingMachines.Api.Data;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class DevelopmentDatabaseConfigurationTests
{
    [Fact]
    public void DevelopmentUsesConfiguredSqlite()
    {
        Assert.True(DevelopmentDatabaseConfiguration.ShouldUseSqlite(
            isDevelopment: true,
            sqliteExplicitlyRequested: false,
            defaultConnection: "Data Source=dev.db"));
    }

    [Fact]
    public void DevelopmentUsesLocalSqliteWhenSharedConnectionIsPlaceholder()
    {
        Assert.True(DevelopmentDatabaseConfiguration.ShouldUseSqlite(
            isDevelopment: true,
            sqliteExplicitlyRequested: false,
            defaultConnection: "Host=YOUR_NEON_HOST;Database=YOUR_DATABASE;Username=YOUR_USERNAME;Password=YOUR_PASSWORD"));
    }

    [Theory]
    [InlineData("Host=localhost;Database=machines;Username=service;Password=synthetic")]
    [InlineData("Host=db.production.internal;Database=machines;Username=service;Password=synthetic")]
    public void DevelopmentRejectsPostgresInsteadOfConnectingSilently(string connectionString)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            DevelopmentDatabaseConfiguration.ShouldUseSqlite(
                isDevelopment: true,
                sqliteExplicitlyRequested: false,
                defaultConnection: connectionString));

        Assert.Contains("somente SQLite", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NonDevelopmentDoesNotSelectSqlite()
    {
        Assert.False(DevelopmentDatabaseConfiguration.ShouldUseSqlite(
            isDevelopment: false,
            sqliteExplicitlyRequested: true,
            defaultConnection: "Data Source=dev.db"));
    }
}
