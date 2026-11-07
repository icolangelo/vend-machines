using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VendingMachines.Api.Controllers;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class PaymentSettingsHttpTests
{
    [Fact]
    public async Task ConditionalAndLegacyUpdatesUseAuthorizedHttpContract()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using (var setup = new AppDbContext(options)) await setup.Database.EnsureCreatedAsync();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        builder.Services.AddDbContext<AppDbContext>(db => db.UseSqlite(connection));
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, HeaderAuthentication>("test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddControllers().AddApplicationPart(typeof(PaymentsController).Assembly).AddControllersAsServices();
        builder.Services.AddScoped(sp => new PaymentsController(sp.GetRequiredService<AppDbContext>(), null!, sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(), sp.GetRequiredService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>()));
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            const string path = "/api/payments/global-settings";
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
            client.DefaultRequestHeaders.Add("X-Test-Role", AppRoles.Operator);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(path, new { applicationFeePercent = 5m })).StatusCode);
            client.DefaultRequestHeaders.Remove("X-Test-Role");
            client.DefaultRequestHeaders.Add("X-Test-Role", AppRoles.PlatformAdmin);
            client.DefaultRequestHeaders.Add("X-Expected-Platform-Fee", "missing");
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(path, new { applicationFeePercent = 5m })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(path, new { applicationFeePercent = 7m })).StatusCode);
            client.DefaultRequestHeaders.Remove("X-Expected-Platform-Fee");
            client.DefaultRequestHeaders.Add("X-Expected-Platform-Fee", "5");
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(path, new { applicationFeePercent = 7m })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(path, new { applicationFeePercent = 9m })).StatusCode);
            client.DefaultRequestHeaders.Remove("X-Expected-Platform-Fee");
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(path, new { applicationFeePercent = 9m })).StatusCode);
            var settings = await client.GetFromJsonAsync<SystemSettings>(path);
            Assert.Equal(9m, settings!.ApplicationFeePercent);
        }
        finally { await app.StopAsync(); }
    }

    private sealed class HeaderAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Request.Headers["X-Test-Role"].ToString();
            if (string.IsNullOrEmpty(role)) return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
