using System.Net;
using Npgsql;

namespace VendingMachines.Api.Data;

public static class ProductionDatabaseConfiguration
{
    private const string InvalidConnectionMessage =
        "A produção exige uma conexão PostgreSQL válida. SQLite, localhost e placeholders não são permitidos fora do desenvolvimento.";
    private const string MissingTlsMessage =
        "A conexão PostgreSQL de produção exige SSL Mode=Require, VerifyCA ou VerifyFull.";

    private static readonly string[] PlaceholderMarkers =
    [
        "change_me", "replace_with", "your_", "placeholder", "example", "dummy", "cole_aqui"
    ];

    public static void Validate(string? connectionString, bool sqliteExplicitlyRequested, bool looksLikeSqlite)
    {
        if (sqliteExplicitlyRequested || looksLikeSqlite || string.IsNullOrWhiteSpace(connectionString) ||
            ContainsPlaceholder(connectionString) ||
            connectionString.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
            connectionString.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(InvalidConnectionMessage);

        NpgsqlConnectionStringBuilder parsed;
        try
        {
            parsed = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException(InvalidConnectionMessage);
        }

        if (string.IsNullOrWhiteSpace(parsed.Host) ||
            string.IsNullOrWhiteSpace(parsed.Database) ||
            string.IsNullOrWhiteSpace(parsed.Username) ||
            ContainsPlaceholder(parsed.Host) ||
            ContainsPlaceholder(parsed.Database) ||
            ContainsPlaceholder(parsed.Username) ||
            parsed.Host.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(IsLoopbackOrUnspecifiedHost))
            throw new InvalidOperationException(InvalidConnectionMessage);

        if (parsed.SslMode is not (SslMode.Require or SslMode.VerifyCA or SslMode.VerifyFull))
            throw new InvalidOperationException(MissingTlsMessage);
    }

    private static bool ContainsPlaceholder(string value) =>
        PlaceholderMarkers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static bool IsLoopbackOrUnspecifiedHost(string host)
    {
        var normalized = host.Trim().Trim('[', ']');
        if (normalized.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!IPAddress.TryParse(normalized, out var address))
            return false;

        return IPAddress.IsLoopback(address) ||
               address.Equals(IPAddress.Any) ||
               address.Equals(IPAddress.IPv6Any);
    }
}
