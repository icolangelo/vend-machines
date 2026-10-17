using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Data;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class AppDbContextFactoryTests
{
    private const string ConnectionVariable = "VENDMACHINES_DESIGNTIME_POSTGRES";

    [Fact]
    public void MissingConnectionStringFailsWithoutChoosingALocalDatabase()
    {
        var previousValue = Environment.GetEnvironmentVariable(ConnectionVariable);
        try
        {
            Environment.SetEnvironmentVariable(ConnectionVariable, null);

            var error = Assert.Throws<InvalidOperationException>(
                () => new AppDbContextFactory().CreateDbContext([]));

            Assert.Contains(ConnectionVariable, error.Message, StringComparison.Ordinal);
            Assert.Contains("Nenhum banco local", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ConnectionVariable, previousValue);
        }
    }

    [Fact]
    public void ExplicitConnectionStringSelectsPostgresProvider()
    {
        var previousValue = Environment.GetEnvironmentVariable(ConnectionVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                ConnectionVariable,
                "Host=localhost;Database=design_time_test;Username=design_time;Password=synthetic-only");

            using var db = new AppDbContextFactory().CreateDbContext([]);

            Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", db.Database.ProviderName);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ConnectionVariable, previousValue);
        }
    }
}
