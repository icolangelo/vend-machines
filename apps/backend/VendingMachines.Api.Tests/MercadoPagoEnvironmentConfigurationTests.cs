using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class MercadoPagoEnvironmentConfigurationTests
{
    [Fact]
    public void AcceptsConfiguredEnvironmentSpecificApplicationAndWebhook()
    {
        MercadoPagoEnvironmentConfiguration.Validate(
            "synthetic-application-id",
            "https://api.vendmachine.test/webhooks/mercadopago");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("REPLACE_WITH_APPLICATION_ID")]
    public void RejectsMissingOrPlaceholderApplicationId(string? clientId)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            MercadoPagoEnvironmentConfiguration.Validate(clientId, "https://api.vendmachine.test/webhooks/mercadopago"));

        Assert.Contains("MercadoPago:ClientId", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://api.vendmachine.test/webhooks/mercadopago")]
    [InlineData("https://localhost/webhooks/mercadopago")]
    [InlineData("https://service.localhost/webhooks/mercadopago")]
    [InlineData("https://0.0.0.0/webhooks/mercadopago")]
    [InlineData("https://api.vendmachine.test")]
    [InlineData("https://api.example.com/webhooks/mercadopago")]
    [InlineData("https://user:password@api.vendmachine.test/webhooks/mercadopago")]
    [InlineData("https://api.vendmachine.test/webhooks/mercadopago?token=secret")]
    public void RejectsInvalidOrNonProductionWebhookUrl(string? webhookUrl)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            MercadoPagoEnvironmentConfiguration.Validate("synthetic-application-id", webhookUrl));

        Assert.Contains("MercadoPago:WebhookUrl", exception.Message, StringComparison.Ordinal);
    }
}
