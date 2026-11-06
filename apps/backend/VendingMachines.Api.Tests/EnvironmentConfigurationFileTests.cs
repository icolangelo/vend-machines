using Microsoft.Extensions.Configuration;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class EnvironmentConfigurationFileTests
{
    private static readonly string[] RuntimeSecretPaths =
    [
        "MercadoPago:ClientSecret",
        "MercadoPago:WebhookSecret",
        "AccountEmail:Password",
        "Jwt:Key",
        "Encryption:Key",
        "Encryption:LegacyKey"
    ];

    private static readonly string[] SensitiveConfigurationSuffixes =
    [
        "ClientSecret",
        "WebhookSecret",
        "Password",
        "AccessToken",
        "RefreshToken",
        "ApiKey",
        "PrivateKey",
        "SigningKey",
        "EncryptionKey"
    ];

    [Fact]
    public void SharedSettingsContainNoDatabasePaymentOrBrowserOrigins()
    {
        var configuration = LoadSettings(environment: null);

        Assert.Null(configuration.GetConnectionString("DefaultConnection"));
        Assert.Null(configuration["MercadoPago:ClientId"]);
        Assert.Null(configuration["MercadoPago:WebhookUrl"]);
        Assert.Empty(GetValues(configuration, "Cors:AllowedOrigins"));
        Assert.Empty(GetValues(configuration, "WebSockets:AllowedOrigins"));
    }

    [Fact]
    public void DevelopmentUsesOnlyLocalDatabaseAndBrowserOrigins()
    {
        var configuration = LoadSettings("Development");
        var origins = GetValues(configuration, "Cors:AllowedOrigins")
            .Concat(GetValues(configuration, "WebSockets:AllowedOrigins"));

        Assert.Contains("Data Source", configuration.GetConnectionString("DefaultConnection"), StringComparison.OrdinalIgnoreCase);
        Assert.Null(configuration["MercadoPago:ClientId"]);
        Assert.Null(configuration["MercadoPago:WebhookUrl"]);
        Assert.All(origins, origin => Assert.StartsWith("http://localhost", origin, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ProductionRuntimeConfigurationValidatesSyntheticPaymentAndHttpsOrigins()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MercadoPago:ClientId"] = "1234567890123456",
                ["MercadoPago:WebhookUrl"] = "https://api.vendmachine.com.br/api/payments/webhook",
                ["Cors:AllowedOrigins:0"] = "https://painel.vendmachine.com.br",
                ["WebSockets:AllowedOrigins:0"] = "https://painel.vendmachine.com.br"
            })
            .Build();
        var corsOrigins = GetValues(configuration, "Cors:AllowedOrigins");
        var webSocketOrigins = GetValues(configuration, "WebSockets:AllowedOrigins");

        MercadoPagoEnvironmentConfiguration.Validate(
            configuration["MercadoPago:ClientId"],
            configuration["MercadoPago:WebhookUrl"]);

        Assert.Equal(corsOrigins, CorsOriginConfiguration.Validate(corsOrigins, requireHttps: true));
        Assert.Equal(corsOrigins, CorsOriginConfiguration.Validate(webSocketOrigins, requireHttps: true));
        Assert.Equal(corsOrigins, webSocketOrigins);
    }

    [Fact]
    public void StagingDoesNotLoadProductionPaymentOrBrowserOrigins()
    {
        var staging = LoadSettings("Staging");

        Assert.Null(staging["MercadoPago:ClientId"]);
        Assert.Null(staging["MercadoPago:WebhookUrl"]);
        Assert.Null(staging.GetConnectionString("DefaultConnection"));
        Assert.Empty(GetValues(staging, "Cors:AllowedOrigins"));
        Assert.Empty(GetValues(staging, "WebSockets:AllowedOrigins"));
    }

    [Fact]
    public void StagingEnvironmentExampleRequiresIsolatedValuesAndContainsNoProductionIdentifiers()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "backend-staging-environment.example");
        var lines = File.ReadAllLines(path);
        var settings = lines
            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith('#') && line.Contains('='))
            .Select(line => (line[..line.IndexOf('=')].Trim(), line[(line.IndexOf('=') + 1)..].Trim()))
            .ToDictionary(item => item.Item1, item => item.Item2, StringComparer.OrdinalIgnoreCase);
        var exampleText = string.Join('\n', lines);

        Assert.Equal("Staging", settings["ASPNETCORE_ENVIRONMENT"]);
        foreach (var key in new[]
        {
            "ConnectionStrings__DefaultConnection",
            "Jwt__Key",
            "Encryption__Key",
            "MercadoPago__ClientId",
            "MercadoPago__ClientSecret",
            "MercadoPago__WebhookSecret",
            "MercadoPago__WebhookUrl",
            "Cors__AllowedOrigins__0",
            "WebSockets__AllowedOrigins__0",
            "AccountEmail__Host",
            "AccountEmail__Username",
            "AccountEmail__Password",
            "AccountEmail__FromAddress"
        })
        {
            Assert.Contains("REPLACE_WITH", settings[key], StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain("api.vendmachine.com.br", exampleText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("app.vendmachine.com.br", exampleText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("7902965695113613", exampleText, StringComparison.Ordinal);
        Assert.Equal("false", settings["Telemetry__RequireDeviceCredentials"]);
        Assert.Equal("false", settings["Encryption__WriteLegacyFormat"]);
        Assert.Equal("false", settings["Encryption__RequireLegacyData"]);
        Assert.Equal("false", settings["ForwardedHeaders__Enabled"]);
        Assert.Equal("1", settings["ForwardedHeaders__ForwardLimit"]);
        Assert.Contains("clientes", exampleText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("KnownProxies", exampleText, StringComparison.Ordinal);
        Assert.DoesNotContain("Encryption__LegacyKey", settings.Keys);
    }

    [Fact]
    public void ProductionEnvironmentExampleExplainsTrustedProxyAndRateLimitRequirements()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "backend-production-environment.example");
        var lines = File.ReadAllLines(path);
        var settings = lines
            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith('#') && line.Contains('='))
            .Select(line => (line[..line.IndexOf('=')].Trim(), line[(line.IndexOf('=') + 1)..].Trim()))
            .ToDictionary(item => item.Item1, item => item.Item2, StringComparer.OrdinalIgnoreCase);
        var exampleText = string.Join('\n', lines);

        Assert.Equal("Production", settings["ASPNETCORE_ENVIRONMENT"]);
        Assert.Equal(settings["ASPNETCORE_ENVIRONMENT"], settings["DOTNET_ENVIRONMENT"]);
        foreach (var key in new[]
        {
            "ConnectionStrings__DefaultConnection",
            "Jwt__Key",
            "Encryption__Key",
            "Encryption__LegacyKey",
            "MercadoPago__ClientId",
            "MercadoPago__ClientSecret",
            "MercadoPago__WebhookSecret",
            "MercadoPago__WebhookUrl",
            "Cors__AllowedOrigins__0",
            "WebSockets__AllowedOrigins__0",
            "AccountEmail__PublicBaseUrl",
            "AccountEmail__Host",
            "AccountEmail__Username",
            "AccountEmail__Password",
            "AccountEmail__FromAddress"
        })
        {
            Assert.Contains("REPLACE_WITH", settings[key], StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("SSL Mode=Require", settings["ConnectionStrings__DefaultConnection"], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(settings["Cors__AllowedOrigins__0"], settings["WebSockets__AllowedOrigins__0"]);
        Assert.Equal("true", settings["AccountEmail__Enabled"]);
        Assert.Equal("false", settings["ForwardedHeaders__Enabled"]);
        Assert.Equal("1", settings["ForwardedHeaders__ForwardLimit"]);
        Assert.Equal("false", settings["Telemetry__RequireDeviceCredentials"]);
        Assert.Equal("40", settings["Telemetry__ApplicationStoppingDrainTimeoutSeconds"]);
        Assert.Equal(string.Empty, settings["Telemetry__LegacyApiBaseUrl"]);
        Assert.DoesNotContain("7902965695113613", exampleText, StringComparison.Ordinal);
        Assert.Contains("clientes", exampleText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("KnownProxies", exampleText, StringComparison.Ordinal);
        Assert.Contains("homologar firmware MDB", exampleText, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Development")]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void JsonSettingsDoNotContainRuntimeSecretsOrNonDevelopmentDatabaseConnections(string? environment)
    {
        var configuration = LoadSettings(environment);

        foreach (var secretPath in RuntimeSecretPaths)
            Assert.True(string.IsNullOrWhiteSpace(configuration[secretPath]), $"{secretPath} deve vir do ambiente de execução, não de appsettings.");

        if (!string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase))
            Assert.True(string.IsNullOrWhiteSpace(configuration.GetConnectionString("DefaultConnection")), "Conexão não local deve vir do ambiente de execução, não de appsettings.");
    }

    [Fact]
    public void EveryAppSettingsFileOmitsRuntimeSecretsAndNonDevelopmentDatabaseConnections()
    {
        var settingsFiles = Directory.GetFiles(AppContext.BaseDirectory, "appsettings*.json", SearchOption.TopDirectoryOnly);
        Assert.Contains(settingsFiles, file => string.Equals(Path.GetFileName(file), "appsettings.json", StringComparison.OrdinalIgnoreCase));

        foreach (var file in settingsFiles)
        {
            using var stream = File.OpenRead(file);
            var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();

            foreach (var entry in configuration.AsEnumerable())
            {
                if (string.IsNullOrWhiteSpace(entry.Value))
                    continue;

                Assert.False(
                    SensitiveConfigurationSuffixes.Any(suffix => entry.Key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)),
                    $"{Path.GetFileName(file)}:{entry.Key} não pode conter segredo versionado.");
            }

            foreach (var secretPath in RuntimeSecretPaths)
                Assert.True(string.IsNullOrWhiteSpace(configuration[secretPath]), $"{Path.GetFileName(file)}:{secretPath} deve vir do ambiente de execução, não de appsettings.");

            if (!string.Equals(Path.GetFileName(file), "appsettings.Development.json", StringComparison.OrdinalIgnoreCase))
                Assert.True(string.IsNullOrWhiteSpace(configuration.GetConnectionString("DefaultConnection")), $"{Path.GetFileName(file)} não pode conter conexão de banco.");
        }
    }

    private static IConfiguration LoadSettings(string? environment)
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false);

        if (environment is not null)
            builder.AddJsonFile($"appsettings.{environment}.json", optional: true, reloadOnChange: false);

        return builder.Build();
    }

    private static string[] GetValues(IConfiguration configuration, string sectionPath) =>
        configuration.GetSection(sectionPath).GetChildren()
            .Select(child => child.Value)
            .OfType<string>()
            .ToArray();
}
