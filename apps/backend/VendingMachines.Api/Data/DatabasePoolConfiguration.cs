using Npgsql;

namespace VendingMachines.Api.Data;

public static class DatabasePoolConfiguration
{
    public const int MinimumMaximumPoolSize = 1;
    public const int MaximumMaximumPoolSize = 1000;

    public static string ApplyMaximumPoolSize(string? connectionString, int maximumPoolSize)
    {
        if (maximumPoolSize is < MinimumMaximumPoolSize or > MaximumMaximumPoolSize)
        {
            throw new InvalidOperationException(
                $"Database:MaximumPoolSize precisa estar entre {MinimumMaximumPoolSize} e {MaximumMaximumPoolSize}.");
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("ConnectionStrings:DefaultConnection é obrigatória para limitar o pool PostgreSQL.");
        }

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (!builder.Pooling)
        {
            throw new InvalidOperationException("Pooling precisa estar habilitado para aplicar Database:MaximumPoolSize.");
        }

        builder.MaxPoolSize = maximumPoolSize;
        return builder.ConnectionString;
    }
}
