using Microsoft.Extensions.DependencyInjection;

namespace VendingMachines.Api.Services;

public static class TelemetryConnectionServiceCollectionExtensions
{
    public static IServiceCollection AddTelemetryConnectionOperations(this IServiceCollection services)
    {
        services.AddSingleton<TelemetryConnectionManager>();
        services.AddSingleton<ITelemetryCommandSender>(provider =>
            provider.GetRequiredService<TelemetryConnectionManager>());
        services.AddSingleton<ITelemetryConnectionOperations>(provider =>
            provider.GetRequiredService<TelemetryConnectionManager>());
        return services;
    }
}
