namespace VendingMachines.Api.Services;

public static class CorsOriginConfiguration
{
    private static readonly string[] ProductionPlaceholderMarkers =
    [
        "localhost", ".localhost", "127.0.0.1", "0.0.0.0", "example", "placeholder", "change_me", "your_"
    ];

    public static string[] Validate(IEnumerable<string>? configuredOrigins, bool requireHttps)
    {
        var origins = configuredOrigins?.ToArray() ?? Array.Empty<string>();
        if (origins.Length == 0)
            throw new InvalidOperationException("Configure ao menos uma origem em Cors:AllowedOrigins.");

        var validated = new List<string>(origins.Length);
        foreach (var configuredOrigin in origins)
        {
            if (string.IsNullOrWhiteSpace(configuredOrigin) ||
                !Uri.TryCreate(configuredOrigin.Trim(), UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                string.IsNullOrWhiteSpace(uri.Host) ||
                (uri.AbsolutePath != "/" && uri.AbsolutePath.Length > 0) ||
                !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment) ||
                !string.IsNullOrEmpty(uri.UserInfo))
            {
                throw new InvalidOperationException("Origens permitidas do navegador devem ser HTTP/HTTPS válidas, sem caminho, query, fragmento ou credenciais.");
            }

            if (requireHttps &&
                (uri.Scheme != Uri.UriSchemeHttps ||
                 uri.IsLoopback ||
                 ProductionPlaceholderMarkers.Any(marker =>
                     uri.Host.Contains(marker, StringComparison.OrdinalIgnoreCase))))
            {
                throw new InvalidOperationException("Origens fora de Development em CORS e WebSocket devem usar HTTPS e não podem ser localhost ou placeholders.");
            }

            validated.Add(uri.GetLeftPart(UriPartial.Authority));
        }

        return validated.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
