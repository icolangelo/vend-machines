using System.Text.RegularExpressions;

namespace VendingMachines.Api.Services;

public static class PaymentLogRedactor
{
    private const string SensitiveIntegrationMessage =
        "Falha na integração de pagamento. Detalhes sensíveis foram omitidos.";
    private const string InternalIntegrationMessage =
        "Detalhes internos da integração de pagamento foram omitidos.";
    private const string SensitiveKeyPattern =
        "access[_-]?token|refresh[_-]?token|client[_-]?secret|webhook[_-]?secret|oauth[_-]?state|authorization[_-]?code|authorization|password|passwd|credential|api[_-]?key|private[_-]?key|secret|token|signature|cookie";

    private static readonly string[] SensitivePrefixes =
    [
        "Erro retornado pelo Mercado Pago:",
        "Falha na integração real do Mercado Pago:",
        "Falha ao solicitar estorno na API do Mercado Pago:",
        "Erro interno ao tentar estorno no Mercado Pago:",
        "Falha ao validar o dono do token OAuth em /users/me:"
    ];

    private static readonly string[] InternalPrefixes =
    [
        "Usando token OAuth salvo na integração",
        "Token OAuth validado no Mercado Pago.",
        "Token OAuth divergente.",
        "QR Code Pix gerado com sucesso pelo Mercado Pago.",
        "Estorno processado com sucesso na API do Mercado Pago."
    ];

    private static readonly Regex BearerTokenPattern = new(
        @"\bBearer\s+[A-Za-z0-9._~+/-]+=*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex SensitiveAssignmentPattern = new(
        @"(?<key>\b(?:" + SensitiveKeyPattern + @")\b)(?<separator>\s*[:=]\s*)(?:Bearer\s+)?(?:""[^""]*""|'[^']*'|[^,\s;&]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex JsonSensitiveAssignmentPattern = new(
        "\"(?<key>" + SensitiveKeyPattern + ")\"(?<separator>\\s*:\\s*)\"[^\"]*\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex MercadoPagoTokenPattern = new(
        @"\bAPP_USR-[A-Za-z0-9_-]{12,}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex JwtPattern = new(
        @"\beyJ[A-Za-z0-9_-]*\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex EmailPattern = new(
        @"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    public static string RedactMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return string.Empty;

        if (SensitivePrefixes.Any(prefix => message.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            return SensitiveIntegrationMessage;

        if (InternalPrefixes.Any(prefix => message.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            return InternalIntegrationMessage;

        var redacted = JsonSensitiveAssignmentPattern.Replace(message, match =>
            $"\"{match.Groups["key"].Value}\"{match.Groups["separator"].Value}\"[REDACTED]\"");
        redacted = SensitiveAssignmentPattern.Replace(redacted, match =>
            $"{match.Groups["key"].Value}{match.Groups["separator"].Value}[REDACTED]");
        redacted = BearerTokenPattern.Replace(redacted, "Bearer [REDACTED]");
        redacted = MercadoPagoTokenPattern.Replace(redacted, "[REDACTED]");
        redacted = JwtPattern.Replace(redacted, "[REDACTED]");
        return EmailPattern.Replace(redacted, "[REDACTED_EMAIL]");
    }
}
