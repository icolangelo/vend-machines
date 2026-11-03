namespace VendingMachines.Api.Data;

public static class DevelopmentDatabaseConfiguration
{
    public static bool ShouldUseSqlite(
        bool isDevelopment,
        bool sqliteExplicitlyRequested,
        string? defaultConnection)
    {
        if (!isDevelopment)
            return false;

        if (sqliteExplicitlyRequested || LooksLikeSqlite(defaultConnection) || IsPlaceholder(defaultConnection))
            return true;

        throw new InvalidOperationException(
            "Development aceita somente SQLite; configure um banco local separado em ConnectionStrings:SqliteConnection.");
    }

    private static bool LooksLikeSqlite(string? connectionString) =>
        connectionString?.Contains("Data Source", StringComparison.OrdinalIgnoreCase) == true ||
        connectionString?.Contains(".db", StringComparison.OrdinalIgnoreCase) == true ||
        connectionString?.Contains("sqlite", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsPlaceholder(string? connectionString) =>
        string.IsNullOrWhiteSpace(connectionString) ||
        new[] { "YOUR_NEON_HOST", "YOUR_USERNAME", "YOUR_PASSWORD", "REPLACE_WITH", "CHANGE_ME", "EXAMPLE" }
            .Any(marker => connectionString.Contains(marker, StringComparison.OrdinalIgnoreCase));
}
