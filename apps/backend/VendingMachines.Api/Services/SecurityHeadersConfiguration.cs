using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace VendingMachines.Api.Services;

public static class SecurityHeadersConfiguration
{
    public static void ConfigureKestrel(KestrelServerOptions options) => options.AddServerHeader = false;
}
