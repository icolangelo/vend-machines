using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace VendingMachines.Api.Services;

public sealed record LegacyTelemetryRelayResult(int StatusCode, string? ContentType, string Content);

/// <summary>
/// Forwards an already-authorized HTTP operation to the API instance that still owns
/// a legacy MDB socket. The original bearer token and request ID are preserved; the
/// relay never retries a request whose result may be uncertain.
/// </summary>
public sealed class LegacyTelemetryRequestRelay
{
    private const string ClientName = "TelemetryLegacyOwner";
    public const string RelayHopHeaderName = "X-VendMachines-Legacy-Relay-Hop";
    public const string WebhookRelayHopHeaderName = "X-VendMachines-Legacy-Webhook-Relay-Hop";
    private const string RelayHopHeaderValue = "1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan ReadinessCacheDuration = TimeSpan.FromSeconds(5);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Uri? _baseAddress;
    private readonly SemaphoreSlim _readinessLock = new(1, 1);
    private DateTime _readinessCheckedAtUtc = DateTime.MinValue;
    private bool _legacyApiReady;

    public LegacyTelemetryRequestRelay(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        var configuredAddress = configuration["Telemetry:LegacyApiBaseUrl"];
        if (string.IsNullOrWhiteSpace(configuredAddress)) return;

        if (!Uri.TryCreate(configuredAddress, UriKind.Absolute, out var address) ||
            (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(address.UserInfo) ||
            !string.IsNullOrEmpty(address.Query) ||
            !string.IsNullOrEmpty(address.Fragment) ||
            address.AbsolutePath != "/")
        {
            throw new InvalidOperationException(
                "Telemetry:LegacyApiBaseUrl deve ser uma origem HTTP(S) absoluta, sem credenciais, caminho, query ou fragmento.");
        }

        _baseAddress = address;
    }

    public bool IsConfigured => _baseAddress is not null;

    public async Task<bool> IsLegacyApiReadyAsync(CancellationToken cancellationToken)
    {
        if (_baseAddress is null) return false;
        if (DateTime.UtcNow - _readinessCheckedAtUtc < ReadinessCacheDuration)
            return _legacyApiReady;

        await _readinessLock.WaitAsync(cancellationToken);
        try
        {
            if (DateTime.UtcNow - _readinessCheckedAtUtc < ReadinessCacheDuration)
                return _legacyApiReady;

            var ready = false;
            try
            {
                // The currently deployed API exposes /health and reports "Healthy".
                // Accept "Ready" as well for compatible newer implementations of this endpoint.
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_baseAddress, "health"));
                request.Headers.TryAddWithoutValidation(RelayHopHeaderName, RelayHopHeaderValue);
                using var response = await _httpClientFactory.CreateClient(ClientName)
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.IsSuccessStatusCode && response.Content is not null)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                    ready = document.RootElement.TryGetProperty("status", out var status) &&
                            status.ValueKind == JsonValueKind.String &&
                            status.GetString() is { } value &&
                            (string.Equals(value, "Healthy", StringComparison.Ordinal) ||
                             string.Equals(value, "Ready", StringComparison.Ordinal));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or JsonException)
            {
                ready = false;
            }

            _legacyApiReady = ready;
            _readinessCheckedAtUtc = DateTime.UtcNow;
            return ready;
        }
        finally
        {
            _readinessLock.Release();
        }
    }

    public async Task<LegacyTelemetryRelayResult?> TryForwardAsync(
        HttpContext context,
        string relativePath,
        object? requestBody,
        CancellationToken cancellationToken)
    {
        if (_baseAddress is null) return null;
        if (context.Request.Headers.Authorization.Count != 1 ||
            !AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization[0], out var authorization) ||
            !string.Equals(authorization.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(authorization.Parameter))
        {
            return new LegacyTelemetryRelayResult(
                StatusCodes.Status503ServiceUnavailable,
                "application/json",
                "{\"code\":\"legacy_owner_auth_unavailable\"}");
        }

        var target = new Uri(_baseAddress, relativePath.TrimStart('/'));
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target);
        request.Headers.Authorization = authorization;
        request.Headers.TryAddWithoutValidation(RelayHopHeaderName, RelayHopHeaderValue);
        if (context.Request.Headers.Accept.Count > 0)
        {
            request.Headers.TryAddWithoutValidation("Accept", context.Request.Headers.Accept.ToArray());
        }

        if (requestBody is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(requestBody, JsonOptions),
                Encoding.UTF8,
                "application/json");
        }

        try
        {
            using var response = await _httpClientFactory.CreateClient(ClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var responseContent = response.Content;
            var content = responseContent is null
                ? string.Empty
                : await responseContent.ReadAsStringAsync(cancellationToken);
            return new LegacyTelemetryRelayResult(
                (int)response.StatusCode,
                responseContent?.Headers.ContentType?.ToString(),
                content);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            return new LegacyTelemetryRelayResult(
                StatusCodes.Status504GatewayTimeout,
                "application/json",
                "{\"code\":\"legacy_owner_outcome_unknown\"}");
        }
    }

    public async Task<LegacyTelemetryRelayResult?> TryForwardWebhookAsync(
        HttpContext context,
        string rawJsonBody,
        CancellationToken cancellationToken)
    {
        if (_baseAddress is null) return null;

        var relativePath = $"webhooks/mercadopago{context.Request.QueryString.Value}";
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseAddress, relativePath));
        request.Headers.TryAddWithoutValidation(WebhookRelayHopHeaderName, RelayHopHeaderValue);
        request.Content = new StringContent(
            rawJsonBody,
            Encoding.UTF8,
            context.Request.ContentType?.Split(';', 2)[0] is { Length: > 0 } mediaType
                ? mediaType
                : "application/json");

        foreach (var headerName in new[] { "x-signature", "x-request-id", "user-agent" })
        {
            if (context.Request.Headers.TryGetValue(headerName, out var values) && values.Count > 0)
                request.Headers.TryAddWithoutValidation(headerName, values.ToArray());
        }

        try
        {
            using var response = await _httpClientFactory.CreateClient(ClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var content = response.Content is null
                ? string.Empty
                : await response.Content.ReadAsStringAsync(cancellationToken);
            return new LegacyTelemetryRelayResult(
                (int)response.StatusCode,
                response.Content?.Headers.ContentType?.ToString(),
                content);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            return new LegacyTelemetryRelayResult(
                StatusCodes.Status504GatewayTimeout,
                "application/json",
                "{\"code\":\"legacy_owner_webhook_outcome_unknown\"}");
        }
    }
}
