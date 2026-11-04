using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class LegacyOwnerStartupGuardTests
{
    [Fact]
    public async Task AllowsStartupWithoutRelayWhenNoRecentLegacySocketsExist()
    {
        await using var scenario = await CreateScenarioAsync();
        var relay = CreateRelay(scenario, configured: false, HttpStatusCode.ServiceUnavailable, "{}");

        await LegacyOwnerStartupGuard.EnsureReadyBeforeWorkersAsync(
            scenario.Db, relay, CancellationToken.None);

        Assert.Equal(0, scenario.Handler.RequestCount);
    }

    [Fact]
    public async Task BlocksStartupBeforeWorkersWhenRecentLegacySocketHasNoRelay()
    {
        await using var scenario = await CreateScenarioAsync(legacyConnectionAge: TimeSpan.Zero);
        var relay = CreateRelay(scenario, configured: false, HttpStatusCode.OK, "{\"status\":\"Healthy\"}");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LegacyOwnerStartupGuard.EnsureReadyBeforeWorkersAsync(
                scenario.Db, relay, CancellationToken.None));

        Assert.Contains("relay privado saudável", exception.Message);
        Assert.Equal(0, scenario.Handler.RequestCount);
    }

    [Fact]
    public async Task BlocksStartupWhenPrivateLegacyApiIsUnhealthy()
    {
        await using var scenario = await CreateScenarioAsync(legacyConnectionAge: TimeSpan.Zero);
        var relay = CreateRelay(scenario, configured: true, HttpStatusCode.ServiceUnavailable, "{\"status\":\"NotReady\"}");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LegacyOwnerStartupGuard.EnsureReadyBeforeWorkersAsync(
                scenario.Db, relay, CancellationToken.None));

        Assert.Equal(1, scenario.Handler.RequestCount);
        Assert.Equal("http://legacy-api.internal/health", scenario.Handler.RequestUri?.ToString());
    }

    [Fact]
    public async Task AllowsStartupWhenPrivateLegacyApiIsHealthy()
    {
        await using var scenario = await CreateScenarioAsync(legacyConnectionAge: TimeSpan.Zero);
        var relay = CreateRelay(scenario, configured: true, HttpStatusCode.OK, "{\"status\":\"Healthy\"}");

        await LegacyOwnerStartupGuard.EnsureReadyBeforeWorkersAsync(
            scenario.Db, relay, CancellationToken.None);

        Assert.Equal(1, scenario.Handler.RequestCount);
        Assert.Equal("http://legacy-api.internal/health", scenario.Handler.RequestUri?.ToString());
    }

    [Fact]
    public async Task BlocksStartupWhenRecentOnlineSocketHasUnknownOwnerRouting()
    {
        await using var scenario = await CreateScenarioAsync(
            legacyConnectionAge: TimeSpan.Zero,
            unknownOwner: true);
        var relay = CreateRelay(scenario, configured: false, HttpStatusCode.ServiceUnavailable, "{}");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LegacyOwnerStartupGuard.EnsureReadyBeforeWorkersAsync(
                scenario.Db, relay, CancellationToken.None));

        Assert.Contains("owner/lease/rota válidos", exception.Message);
        Assert.Equal(0, scenario.Handler.RequestCount);
    }

    [Fact]
    public async Task AllowsStartupWhenRecentOnlineSocketHasValidOwnerLease()
    {
        await using var scenario = await CreateScenarioAsync(
            legacyConnectionAge: TimeSpan.Zero,
            validOwner: true);
        var relay = CreateRelay(scenario, configured: false, HttpStatusCode.ServiceUnavailable, "{}");

        await LegacyOwnerStartupGuard.EnsureReadyBeforeWorkersAsync(
            scenario.Db, relay, CancellationToken.None);

        Assert.Equal(0, scenario.Handler.RequestCount);
    }

    [Fact]
    public async Task IgnoresExpiredLegacyPresenceAndDoesNotRequireRelay()
    {
        await using var scenario = await CreateScenarioAsync(
            legacyConnectionAge: MachineConnectionFreshness.Window + TimeSpan.FromSeconds(1));
        var relay = CreateRelay(scenario, configured: false, HttpStatusCode.ServiceUnavailable, "{}");

        await LegacyOwnerStartupGuard.EnsureReadyBeforeWorkersAsync(
            scenario.Db, relay, CancellationToken.None);

        Assert.Equal(0, scenario.Handler.RequestCount);
    }

    private static async Task<TestScenario> CreateScenarioAsync(
        TimeSpan? legacyConnectionAge = null,
        bool unknownOwner = false,
        bool validOwner = false)
    {
        var connection = new SqliteConnection($"Data Source=legacy-startup-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        if (legacyConnectionAge.HasValue)
        {
            var company = new Company { Name = "Empresa de teste" };
            var machine = new Machine
            {
                Id = "legacy-startup-machine",
                Name = "Máquina legada",
                SerialNumber = "LEGACY-STARTUP-1",
                NormalizedSerialNumber = "LEGACY-STARTUP-1",
                CompanyId = company.Id
            };
            db.Companies.Add(company);
            db.Machines.Add(machine);
            var connectionId = "legacy-socket";
            var ownerNodeId = unknownOwner || validOwner ? "test-owner" : null;
            db.MachineConnectionStates.Add(new MachineConnectionState
            {
                MachineId = machine.Id,
                CompanyId = company.Id,
                IsOnline = true,
                ConnectionId = connectionId,
                LastSeenAt = DateTime.UtcNow - legacyConnectionAge.Value,
                OwnerNodeId = ownerNodeId,
                OwnerConnectionId = unknownOwner ? "different-socket" : validOwner ? connectionId : null,
                CommandRoutingVersion = unknownOwner || validOwner
                    ? TelemetryConnectionManager.CurrentCommandRoutingVersion
                    : null,
                OwnerLeaseExpiresAt = unknownOwner || validOwner
                    ? DateTime.UtcNow.AddMinutes(1)
                    : null
            });
            await db.SaveChangesAsync();
        }

        return new TestScenario(connection, db, new LegacyApiHealthHandler(HttpStatusCode.OK, "{}"));
    }

    private static LegacyTelemetryRequestRelay CreateRelay(
        TestScenario scenario,
        bool configured,
        HttpStatusCode statusCode,
        string responseBody)
    {
        scenario.Handler.StatusCodeToReturn = statusCode;
        scenario.Handler.ResponseBody = responseBody;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configured
                ? new Dictionary<string, string?> { ["Telemetry:LegacyApiBaseUrl"] = "http://legacy-api.internal" }
                : new Dictionary<string, string?>())
            .Build();
        return new LegacyTelemetryRequestRelay(new FixedHttpClientFactory(new HttpClient(scenario.Handler)), configuration);
    }

    private sealed class TestScenario(SqliteConnection connection, AppDbContext db, LegacyApiHealthHandler handler) : IAsyncDisposable
    {
        public AppDbContext Db { get; } = db;
        public LegacyApiHealthHandler Handler { get; } = handler;

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class FixedHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class LegacyApiHealthHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        public HttpStatusCode StatusCodeToReturn { get; set; } = statusCode;
        public string ResponseBody { get; set; } = body;
        public int RequestCount { get; private set; }
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(StatusCodeToReturn)
            {
                Content = new StringContent(ResponseBody, Encoding.UTF8, "application/json")
            });
        }
    }
}
