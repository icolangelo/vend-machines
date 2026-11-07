namespace VendingMachines.Api.Services;

public sealed class WebSocketOriginPolicy
{
    private readonly HashSet<string> _allowedOrigins;

    public WebSocketOriginPolicy(IEnumerable<string> origins)
    {
        _allowedOrigins = origins
            .Select(NormalizeOrigin)
            .Where(origin => origin is not null)
            .Select(origin => origin!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public bool Allows(string? origin)
    {
        var normalizedOrigin = NormalizeOrigin(origin);
        return normalizedOrigin is not null && _allowedOrigins.Contains(normalizedOrigin);
    }

    public static string[] ResolveOrigins(IEnumerable<string>? configuredOrigins, IEnumerable<string> corsOrigins)
    {
        var configured = configuredOrigins?.ToArray() ?? Array.Empty<string>();
        var selected = configured.Length > 0 ? configured : corsOrigins;
        var resolved = selected
            .Select(NormalizeOrigin)
            .Where(origin => origin is not null)
            .Select(origin => origin!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (resolved.Length == 0)
            throw new InvalidOperationException("Configure origens HTTP/HTTPS válidas para os WebSockets do painel.");

        return resolved;
    }

    private static string? NormalizeOrigin(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
            return null;

        if ((uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            (uri.AbsolutePath != "/" && uri.AbsolutePath.Length > 0) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            return null;
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }
}
