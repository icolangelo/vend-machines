using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using VendingMachines.Api.Controllers;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class LegacyTelemetryRequestRelayTests
{
    [Fact]
    public async Task ManualCommandForLegacySocketIsForwardedOnceWithOriginalBearerAndRequestId()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var companyId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var machine = new Machine
        {
            Id = "MACHINE-LEGACY-RELAY",
            Name = "Máquina legada",
            SerialNumber = "SERIAL-LEGACY-RELAY",
            NormalizedSerialNumber = "SERIAL-LEGACY-RELAY",
            CompanyId = companyId
        };
        db.Companies.Add(new Company { Id = companyId, Name = "Empresa legada" });
        db.Machines.Add(machine);
        db.MachineConnectionStates.Add(new MachineConnectionState
        {
            MachineId = machine.Id,
            CompanyId = companyId,
            MonitoringEnabled = true,
            IsOnline = true,
            ConnectionId = "legacy-socket",
            LastSeenAt = DateTime.UtcNow,
            OwnerNodeId = null,
            OwnerConnectionId = null,
            CommandRoutingVersion = null
        });
        await db.SaveChangesAsync();

        var handler = new CaptureHttpHandler();
        var relay = new LegacyTelemetryRequestRelay(
            new FixedHttpClientFactory(new HttpClient(handler)),
            CreateConfiguration(("Telemetry:LegacyApiBaseUrl", "http://legacy-api.internal")));
        var controller = new TelemetryController(
            db,
            orchestrator: null!,
            connections: null!,
            panelHub: null!,
            tickets: null!,
            mdbStatusRequests: null!,
            mdbOptions: Options.Create(new MdbStatusOptions()),
            commandSender: null,
            scopeFactory: null,
            legacyTelemetryRelay: relay);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "POST";
        httpContext.Request.Headers.Authorization = "Bearer original-user-token";
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("company_id", companyId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Role, AppRoles.Admin)
        ], "test"));
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var requestId = Guid.NewGuid();
        var response = await controller.SendCommand(
            machine.Id,
            new SendCommandRequest { RequestId = requestId, Command = "open", Data = new { product = "water" } },
            CancellationToken.None);

        var forwarded = Assert.IsType<ContentResult>(response);
        Assert.Equal((int)HttpStatusCode.Accepted, forwarded.StatusCode);
        Assert.Equal("application/json; charset=utf-8", forwarded.ContentType);
        Assert.Equal("{\"status\":\"accepted\"}", forwarded.Content);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("http://legacy-api.internal/api/telemetry/machines/MACHINE-LEGACY-RELAY/commands", handler.RequestUri?.ToString());
        Assert.Equal("Bearer", handler.Authorization?.Scheme);
        Assert.Equal("original-user-token", handler.Authorization?.Parameter);
        Assert.Equal("1", handler.Headers[LegacyTelemetryRequestRelay.RelayHopHeaderName]);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(requestId, body.RootElement.GetProperty("requestId").GetGuid());
        var retried = await controller.SendCommand(
            machine.Id,
            new SendCommandRequest { RequestId = requestId, Command = "open", Data = new { product = "water" } },
            CancellationToken.None);
        Assert.IsType<AcceptedResult>(retried);
        Assert.Equal(1, handler.RequestCount);
        var command = await db.TelemetryCommands.SingleAsync();
        Assert.Equal(requestId, command.Id);
        Assert.Equal("acknowledged", command.Status);
        Assert.Equal(1, command.Attempts);
        Assert.Empty(await db.TelemetryCommandDispatches.ToListAsync());

        handler.StatusCodeToReturn = HttpStatusCode.GatewayTimeout;
        handler.ResponseContent = "{\"code\":\"legacy_owner_outcome_unknown\"}";
        var uncertainRequestId = Guid.NewGuid();
        var uncertain = await controller.SendCommand(
            machine.Id,
            new SendCommandRequest { RequestId = uncertainRequestId, Command = "close", Data = new { reason = "test" } },
            CancellationToken.None);

        var uncertainResult = Assert.IsType<ContentResult>(uncertain);
        Assert.Equal(StatusCodes.Status504GatewayTimeout, uncertainResult.StatusCode);
        Assert.Equal(2, handler.RequestCount);
        var uncertainCommand = await db.TelemetryCommands.SingleAsync(item => item.Id == uncertainRequestId);
        Assert.Equal("failed", uncertainCommand.Status);
        Assert.Equal("command_outcome_unknown", uncertainCommand.Error);

        var uncertainRetry = await controller.SendCommand(
            machine.Id,
            new SendCommandRequest { RequestId = uncertainRequestId, Command = "close", Data = new { reason = "test" } },
            CancellationToken.None);

        var replayedUncertain = Assert.IsType<ObjectResult>(uncertainRetry);
        Assert.Equal(StatusCodes.Status504GatewayTimeout, replayedUncertain.StatusCode);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task LegacyRelayDoesNotInventAuthenticationWhenBearerIsMissing()
    {
        var handler = new CaptureHttpHandler();
        var relay = new LegacyTelemetryRequestRelay(
            new FixedHttpClientFactory(new HttpClient(handler)),
            CreateConfiguration(("Telemetry:LegacyApiBaseUrl", "http://legacy-api.internal")));

        var response = await relay.TryForwardAsync(
            new DefaultHttpContext(),
            "api/telemetry/machines/M1/commands",
            new { requestId = Guid.NewGuid(), command = "open" },
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, response?.StatusCode);
        Assert.Contains("legacy_owner_auth_unavailable", response?.Content);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task LegacyOwnerHealthProbeCarriesHopMarker()
    {
        var handler = new CaptureHttpHandler
        {
            StatusCodeToReturn = HttpStatusCode.OK,
            ResponseContent = "{\"status\":\"Healthy\"}"
        };
        var relay = new LegacyTelemetryRequestRelay(
            new FixedHttpClientFactory(new HttpClient(handler)),
            CreateConfiguration(("Telemetry:LegacyApiBaseUrl", "http://legacy-api.internal")));

        var ready = await relay.IsLegacyApiReadyAsync(CancellationToken.None);

        Assert.True(ready);
        Assert.Equal("1", handler.Headers[LegacyTelemetryRequestRelay.RelayHopHeaderName]);
        Assert.Equal("http://legacy-api.internal/health", handler.RequestUri?.ToString());
    }

    [Fact]
    public async Task SelfReferentialOwnerEndpointIsNotConsideredHealthy()
    {
        var handler = new CaptureHttpHandler
        {
            StatusCodeToReturn = (HttpStatusCode)508,
            ResponseContent = "{\"code\":\"legacy_relay_loop_blocked\"}"
        };
        var relay = new LegacyTelemetryRequestRelay(
            new FixedHttpClientFactory(new HttpClient(handler)),
            CreateConfiguration(("Telemetry:LegacyApiBaseUrl", "http://candidate-api.internal")));

        var ready = await relay.IsLegacyApiReadyAsync(CancellationToken.None);

        Assert.False(ready);
        Assert.Equal("1", handler.Headers[LegacyTelemetryRequestRelay.RelayHopHeaderName]);
    }

    [Fact]
    public async Task WebhookRelayPreservesProviderQueryBodyAndVerificationHeaders()
    {
        var handler = new CaptureHttpHandler();
        var relay = new LegacyTelemetryRequestRelay(
            new FixedHttpClientFactory(new HttpClient(handler)),
            CreateConfiguration(("Telemetry:LegacyApiBaseUrl", "http://legacy-api.internal")));
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?data.id=123456789&type=payment");
        context.Request.Headers["x-signature"] = "ts=123,v1=abc";
        context.Request.Headers["x-request-id"] = "provider-request-1";
        context.Request.Headers["user-agent"] = "mercadopago-webhook";

        var response = await relay.TryForwardWebhookAsync(
            context,
            "{\"type\":\"payment\",\"data\":{\"id\":\"123456789\"}}",
            CancellationToken.None);

        Assert.Equal((int)HttpStatusCode.Accepted, response?.StatusCode);
        Assert.Equal("http://legacy-api.internal/webhooks/mercadopago?data.id=123456789&type=payment", handler.RequestUri?.ToString());
        Assert.Equal("1", handler.Headers[LegacyTelemetryRequestRelay.RelayHopHeaderName]);
        Assert.Equal("{\"type\":\"payment\",\"data\":{\"id\":\"123456789\"}}", handler.Body);
        Assert.Equal("ts=123,v1=abc", handler.Headers["x-signature"]);
        Assert.Equal("provider-request-1", handler.Headers["x-request-id"]);
        Assert.Equal("mercadopago-webhook", handler.Headers["user-agent"]);
        Assert.Null(handler.Authorization);
    }

    [Fact]
    public async Task LegacyRelayIsDisabledWhenNoPrivateOwnerAddressIsConfigured()
    {
        var handler = new CaptureHttpHandler();
        var relay = new LegacyTelemetryRequestRelay(new FixedHttpClientFactory(new HttpClient(handler)), CreateConfiguration());

        var response = await relay.TryForwardAsync(
            new DefaultHttpContext(),
            "api/telemetry/machines/M1/commands",
            new { requestId = Guid.NewGuid(), command = "open" },
            CancellationToken.None);

        Assert.False(relay.IsConfigured);
        Assert.Null(response);
        Assert.Equal(0, handler.RequestCount);
    }

    private static IConfiguration CreateConfiguration(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.ToDictionary(entry => entry.Key, entry => (string?)entry.Value))
            .Build();

    private sealed class FixedHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class CaptureHttpHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public HttpStatusCode StatusCodeToReturn { get; set; } = HttpStatusCode.Accepted;
        public string ResponseContent { get; set; } = "{\"status\":\"accepted\"}";
        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }
        public System.Net.Http.Headers.AuthenticationHeaderValue? Authorization { get; private set; }
        public string? Body { get; private set; }
        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            Method = request.Method;
            RequestUri = request.RequestUri;
            Authorization = request.Headers.Authorization;
            foreach (var header in request.Headers)
                Headers[header.Key] = string.Join(",", header.Value);
            if (request.Content is not null)
            {
                foreach (var header in request.Content.Headers)
                    Headers[header.Key] = string.Join(",", header.Value);
            }
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(StatusCodeToReturn)
            {
                Content = new StringContent(ResponseContent, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }
}
