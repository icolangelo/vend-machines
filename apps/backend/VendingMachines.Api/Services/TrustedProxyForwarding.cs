using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;

namespace VendingMachines.Api.Services;

public static class TrustedProxyForwarding
{
    public static bool IsEnabled(IConfiguration configuration) =>
        configuration.GetValue<bool>("ForwardedHeaders:Enabled");

    public static ForwardedHeadersOptions CreateOptions(IConfiguration configuration)
    {
        var proxyAddresses = configuration.GetSection("ForwardedHeaders:KnownProxies")
            .GetChildren()
            .Select(section => section.Value?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();

        if (proxyAddresses.Length == 0)
        {
            throw new InvalidOperationException(
                "ForwardedHeaders:KnownProxies deve listar ao menos um IP confiável quando ForwardedHeaders:Enabled=true.");
        }

        var forwardLimit = configuration.GetValue<int?>("ForwardedHeaders:ForwardLimit") ?? 1;
        if (forwardLimit is < 1 or > 5)
        {
            throw new InvalidOperationException("ForwardedHeaders:ForwardLimit deve estar entre 1 e 5.");
        }

        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = forwardLimit
        };
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();

        foreach (var address in proxyAddresses)
        {
            if (!IPAddress.TryParse(address, out var ipAddress))
            {
                throw new InvalidOperationException(
                    $"ForwardedHeaders:KnownProxies contém um IP inválido: '{address}'. Informe endereços IP, não nomes DNS.");
            }

            options.KnownProxies.Add(ipAddress);
            if (ipAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                options.KnownProxies.Add(ipAddress.MapToIPv6());
            }
        }

        return options;
    }
}
