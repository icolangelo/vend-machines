using System.Text.Json;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class PaymentLogRedactorTests
{
    [Fact]
    public void RedactsSensitiveKeyValueAssignmentsAndEmails()
    {
        var message = "Gateway diagnostic: access_token=access-private refresh_token: \"refresh-private\" contact=private@example.test";

        var redacted = PaymentLogRedactor.RedactMessage(message);

        Assert.DoesNotContain("access-private", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh-private", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("private@example.test", redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED_EMAIL]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactsBearerMercadoPagoAndJwtTokens()
    {
        var message = "Authorization: Bearer bearer-private APP_USR-123456789012-secret eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjMifQ.signature-private";

        var redacted = PaymentLogRedactor.RedactMessage(message);

        Assert.DoesNotContain("bearer-private", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("APP_USR-123456789012-secret", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", redacted, StringComparison.Ordinal);
        Assert.Contains("Authorization: [REDACTED]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactsJsonPropertiesAndPreservesValidJson()
    {
        var message = "Gateway response: {\"access_token\":\"json-access-private\",\"refresh_token\":\"json-refresh-private\",\"Authorization\":\"Bearer json-bearer-private\",\"contact\":\"json-private@example.test\"}";

        var redacted = PaymentLogRedactor.RedactMessage(message);

        Assert.DoesNotContain("json-access-private", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("json-refresh-private", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("json-bearer-private", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("json-private@example.test", redacted, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(redacted[redacted.IndexOf('{')..]);
        Assert.Equal("[REDACTED]", document.RootElement.GetProperty("access_token").GetString());
        Assert.Equal("[REDACTED]", document.RootElement.GetProperty("refresh_token").GetString());
        Assert.Equal("[REDACTED]", document.RootElement.GetProperty("Authorization").GetString());
        Assert.Equal("[REDACTED_EMAIL]", document.RootElement.GetProperty("contact").GetString());
    }

    [Fact]
    public void PreservesSafeOperationalMessagesAndKeepsKnownPrefixRedaction()
    {
        const string safe = "Solicitada cobrança Pix de R$ 12,00 na máquina Máquina A.";

        Assert.Equal(safe, PaymentLogRedactor.RedactMessage(safe));
        Assert.Equal(
            "Falha na integração de pagamento. Detalhes sensíveis foram omitidos.",
            PaymentLogRedactor.RedactMessage("Erro retornado pelo Mercado Pago: resposta privada"));
    }
}
