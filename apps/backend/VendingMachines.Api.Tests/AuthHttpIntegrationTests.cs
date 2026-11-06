using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using VendingMachines.Api.Controllers;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class AuthHttpIntegrationTests
{
    [Fact]
    public async Task BrowserCookieContainerStoresAndSendsSecureCookieOnlyThroughHttps()
    {
        using var certificate = FindHttpsDevelopmentCertificate();
        if (certificate == null)
            throw Xunit.Sdk.SkipException.ForSkip("Certificado HTTPS de desenvolvimento ausente; o teste de contrato HTTP continua ativo.");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        builder.Services.AddSingleton(Options.Create(new AuthOptions()));
        builder.Services.AddScoped<AuthCookieService>();
        await using var app = builder.Build();
        app.MapPost("/login", (HttpContext context, AuthCookieService cookies) =>
        {
            cookies.Write(context.Response, "first-refresh-token", DateTime.UtcNow.AddDays(1));
            return Results.Ok();
        });
        app.MapPost("/refresh", (HttpContext context, AuthCookieService cookies) =>
        {
            if (cookies.Read(context.Request) != "first-refresh-token") return Results.Unauthorized();
            cookies.Write(context.Response, "rotated-refresh-token", DateTime.UtcNow.AddDays(1));
            return Results.Ok();
        });
        app.MapPost("/logout", (HttpContext context, AuthCookieService cookies) =>
        {
            if (cookies.Read(context.Request) != "rotated-refresh-token") return Results.Unauthorized();
            cookies.Delete(context.Response);
            return Results.NoContent();
        });
        await app.StartAsync();

        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var port = new Uri(address).Port;
            var handler = new HttpClientHandler
            {
                CookieContainer = new CookieContainer(),
                UseProxy = false,
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
            using var client = new HttpClient(handler) { BaseAddress = new UriBuilder(Uri.UriSchemeHttps, "localhost", port).Uri };

            using var login = await client.PostAsync("/login", content: null);
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            var loginCookie = Assert.Single(login.Headers.GetValues("Set-Cookie"));
            AssertCookieSecurity(loginCookie, "__Host-vm_refresh");
            Assert.Single(handler.CookieContainer.GetCookies(client.BaseAddress!));

            using var refresh = await client.PostAsync("/refresh", content: null);
            Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
            var rotatedCookie = Assert.Single(refresh.Headers.GetValues("Set-Cookie"));
            AssertCookieSecurity(rotatedCookie, "__Host-vm_refresh");
            Assert.Equal("rotated-refresh-token", handler.CookieContainer.GetCookies(client.BaseAddress!)["__Host-vm_refresh"]?.Value);

            using var logout = await client.PostAsync("/logout", content: null);
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
            var deletedCookie = Assert.Single(logout.Headers.GetValues("Set-Cookie"));
            Assert.Contains("expires=Thu, 01 Jan 1970", deletedCookie, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(handler.CookieContainer.GetCookies(client.BaseAddress!).Cast<Cookie>());
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task LoginRefreshProtectedRequestAndLogoutUseTheHttpCookieContract()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var user = new User
        {
            Id = Guid.NewGuid(),
            Name = "Administradora sintética",
            Email = "auth-http@example.test",
            NormalizedEmail = "auth-http@example.test",
            Role = AppRoles.PlatformAdmin,
            IsActive = true,
            Cpf = "52998224725",
            AcceptedPrivacyPolicy = true,
            CreatedAt = DateTime.UtcNow
        };
        user.PasswordHash = new Microsoft.AspNetCore.Identity.PasswordHasher<User>()
            .HashPassword(user, "senha-de-integracao-local-123");

        await using (var setup = new AppDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.Users.Add(user);
            await setup.SaveChangesAsync();
        }

        const string signingKey = "AuthHttpIntegrationTestSigningKey-2026!";
        const string issuer = "AuthHttpIntegrationTests";
        const string audience = "AuthHttpIntegrationTestsClient";
        var authOptions = Microsoft.Extensions.Options.Options.Create(new AuthOptions
        {
            AccessTokenMinutes = 15,
            RefreshTokenDays = 30,
            RefreshReuseGraceSeconds = 10
        });
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = signingKey,
            ["Jwt:Issuer"] = issuer,
            ["Jwt:Audience"] = audience
        });
        builder.Services.AddDbContext<AppDbContext>(db => db.UseSqlite(connection));
        builder.Services.AddSingleton(authOptions);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddScoped<AuthTokenService>();
        builder.Services.AddScoped<RefreshSessionService>();
        builder.Services.AddScoped<AuthCookieService>();
        builder.Services.AddRateLimiter(rateLimiter => rateLimiter.AddPolicy("auth", _ => RateLimitPartition.GetNoLimiter("auth-http-test")));
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwt =>
            {
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = issuer,
                    ValidAudience = audience,
                    IssuerSigningKey = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(signingKey)),
                    ClockSkew = TimeSpan.Zero
                };
            });
        builder.Services.AddAuthorization();
        builder.Services.AddControllers().AddApplicationPart(typeof(AuthController).Assembly);
        await using var app = builder.Build();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();

        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address) };

            using var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new
            {
                email = user.Email,
                password = "senha-de-integracao-local-123"
            });
            Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
            var loginCookies = loginResponse.Headers.GetValues("Set-Cookie").ToArray();
            var loginCookie = Assert.Single(loginCookies);
            AssertCookieSecurity(loginCookie, "__Host-vm_refresh");
            using var loginJson = JsonDocument.Parse(await loginResponse.Content.ReadAsStringAsync());
            var initialAccessToken = loginJson.RootElement.GetProperty("token").GetString();
            Assert.False(string.IsNullOrWhiteSpace(initialAccessToken));
            Assert.DoesNotContain("refresh", loginJson.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);

            using var initialMeRequest = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
            initialMeRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", initialAccessToken);
            using var initialMeResponse = await client.SendAsync(initialMeRequest);
            Assert.Equal(HttpStatusCode.OK, initialMeResponse.StatusCode);

            using var refreshRequest = CreateCookieRequest("/api/auth/refresh", loginCookie);
            using var refreshResponse = await client.SendAsync(refreshRequest);
            Assert.Equal(HttpStatusCode.OK, refreshResponse.StatusCode);
            var rotatedCookie = Assert.Single(refreshResponse.Headers.GetValues("Set-Cookie"));
            AssertCookieSecurity(rotatedCookie, "__Host-vm_refresh");
            Assert.NotEqual(CookieValue(loginCookie), CookieValue(rotatedCookie));
            using var refreshJson = JsonDocument.Parse(await refreshResponse.Content.ReadAsStringAsync());
            var refreshedAccessToken = refreshJson.RootElement.GetProperty("token").GetString();
            Assert.False(string.IsNullOrWhiteSpace(refreshedAccessToken));
            Assert.NotEqual(initialAccessToken, refreshedAccessToken);

            using var refreshedMeRequest = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
            refreshedMeRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", refreshedAccessToken);
            using var refreshedMeResponse = await client.SendAsync(refreshedMeRequest);
            Assert.Equal(HttpStatusCode.OK, refreshedMeResponse.StatusCode);
            using var meJson = JsonDocument.Parse(await refreshedMeResponse.Content.ReadAsStringAsync());
            Assert.Equal(user.Id.ToString(), meJson.RootElement.GetProperty("id").GetString());

            // Simulates a logout request sent before refresh whose response arrives afterward.
            using var logoutRequest = CreateCookieRequest("/api/auth/logout", loginCookie);
            using var logoutResponse = await client.SendAsync(logoutRequest);
            Assert.Equal(HttpStatusCode.NoContent, logoutResponse.StatusCode);
            var expiredCookie = Assert.Single(logoutResponse.Headers.GetValues("Set-Cookie"));
            AssertCookieSecurity(expiredCookie, "__Host-vm_refresh");
            Assert.Contains("expires=Thu, 01 Jan 1970", expiredCookie, StringComparison.OrdinalIgnoreCase);

            using var expiredRefreshRequest = CreateCookieRequest("/api/auth/refresh", rotatedCookie);
            using var expiredRefreshResponse = await client.SendAsync(expiredRefreshRequest);
            Assert.Equal(HttpStatusCode.Unauthorized, expiredRefreshResponse.StatusCode);
            await using var verification = new AppDbContext(options);
            Assert.Equal(0, await verification.RefreshSessions.CountAsync(session => session.RevokedAt == null));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static HttpRequestMessage CreateCookieRequest(string path, string setCookieHeader)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.TryAddWithoutValidation("Cookie", setCookieHeader.Split(';', 2)[0]);
        return request;
    }

    private static X509Certificate2? FindHttpsDevelopmentCertificate()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var now = DateTime.Now;
        var certificate = store.Certificates
            .OfType<X509Certificate2>()
            .Where(candidate => candidate.HasPrivateKey &&
                candidate.Subject.Contains("CN=localhost", StringComparison.OrdinalIgnoreCase) &&
                candidate.NotBefore <= now && candidate.NotAfter > now)
            .OrderByDescending(candidate => candidate.NotAfter)
            .FirstOrDefault();
        return certificate == null ? null : new X509Certificate2(certificate);
    }

    private static void AssertCookieSecurity(string cookie, string name)
    {
        Assert.Contains($"{name}=", cookie, StringComparison.Ordinal);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
    }

    private static string CookieValue(string cookie)
    {
        var value = cookie.Split(';', 2)[0];
        return value[(value.IndexOf('=') + 1)..];
    }
}
