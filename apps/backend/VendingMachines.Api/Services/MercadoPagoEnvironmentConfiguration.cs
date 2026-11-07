using System.Net;

namespace VendingMachines.Api.Services;

public static class MercadoPagoEnvironmentConfiguration
{
    private static readonly string[] PlaceholderMarkers =
    [
        "change_me", "replace_with", "your_", "placeholder", "example", "dummy", "cole_aqui"
    ];

    public static void Validate(string? clientId, string? webhookUrl)
    {
        if (IsMissingOrPlaceholder(clientId))
            throw new InvalidOperationException("MercadoPago:ClientId deve ser configurado para este ambiente.");

        if (IsMissingOrPlaceholder(webhookUrl) ||
            !Uri.TryCreate(webhookUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            IsLocalHost(uri) ||
            uri.AbsolutePath == "/" ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            PlaceholderMarkers.Any(marker => uri.Host.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "MercadoPago:WebhookUrl deve ser uma URL HTTPS válida, específica do ambiente e sem credenciais.");
        }
    }

    private static bool IsMissingOrPlaceholder(string? value) =>
        string.IsNullOrWhiteSpace(value) ||
        PlaceholderMarkers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static bool IsLocalHost(Uri uri)
    {
        var host = uri.Host.Trim('[', ']');
        if (uri.IsLoopback ||
            host.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("::", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        return IPAddress.TryParse(host, out var address) &&
               (IPAddress.IsLoopback(address) ||
                address.Equals(IPAddress.Any) ||
                address.Equals(IPAddress.IPv6Any));
    }
}
