using System.Text.Json;
using System.Text.Json.Nodes;

namespace VendingMachines.Api.Services;

public static class AuditMetadataRedactor
{
    private const string RedactedValue = "[REDACTED]";

    public static string? SerializeRedacted(object? metadata)
    {
        if (metadata is null)
            return null;

        var json = JsonSerializer.Serialize(metadata);
        return RedactJson(json);
    }

    public static string? RedactJson(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
            return metadataJson;

        try
        {
            var node = JsonNode.Parse(metadataJson);
            if (node is not JsonObject and not JsonArray)
                return "\"[METADATA_OMITTED]\"";

            RedactNode(node);
            return node?.ToJsonString();
        }
        catch (JsonException)
        {
            // Dados históricos inválidos não devem ser ecoados sem inspeção.
            return "\"[METADATA_OMITTED]\"";
        }
    }

    private static void RedactNode(JsonNode? node)
    {
        if (node is JsonObject jsonObject)
        {
            foreach (var property in jsonObject.ToList())
            {
                if (IsSensitiveProperty(property.Key))
                    jsonObject[property.Key] = RedactedValue;
                else
                    RedactNode(property.Value);
            }
        }
        else if (node is JsonArray jsonArray)
        {
            foreach (var item in jsonArray)
                RedactNode(item);
        }
    }

    private static bool IsSensitiveProperty(string propertyName)
    {
        var normalized = string.Concat(propertyName.Where(char.IsLetterOrDigit))
            .ToLowerInvariant();

        return normalized.Contains("password", StringComparison.Ordinal) ||
               normalized.Contains("secret", StringComparison.Ordinal) ||
               normalized.Contains("token", StringComparison.Ordinal) ||
               normalized.Contains("credential", StringComparison.Ordinal) ||
               normalized.Contains("authorization", StringComparison.Ordinal) ||
               normalized.Contains("cookie", StringComparison.Ordinal) ||
               normalized.Contains("header", StringComparison.Ordinal) ||
               normalized.Contains("payload", StringComparison.Ordinal) ||
               normalized.Contains("body", StringComparison.Ordinal) ||
               normalized.Contains("apikey", StringComparison.Ordinal) ||
               normalized.Contains("privatekey", StringComparison.Ordinal) ||
               normalized.Contains("signature", StringComparison.Ordinal) ||
               normalized.Contains("email", StringComparison.Ordinal) ||
               normalized.Contains("phone", StringComparison.Ordinal) ||
               normalized.Contains("cpf", StringComparison.Ordinal) ||
               normalized.Contains("cnpj", StringComparison.Ordinal) ||
               normalized.Contains("document", StringComparison.Ordinal);
    }
}
