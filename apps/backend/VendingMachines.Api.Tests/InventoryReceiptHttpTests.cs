using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
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

public sealed class InventoryReceiptHttpTests
{
    [Fact]
    public async Task ReceiptUsesRealHttpJsonAndRequiresSameCompanyUserAndAdminRole()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var companyA = Guid.NewGuid();
        var companyB = Guid.NewGuid();
        var userA = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        const string reason = "Reposição sintética";

        await using (var setup = new AppDbContext(options))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.Companies.AddRange(
                new Company { Id = companyA, Name = "Empresa A", Cnpj = "11111111000111" },
                new Company { Id = companyB, Name = "Empresa B", Cnpj = "22222222000111" });
            setup.Machines.Add(new Machine { Id = "http-machine", Name = "Máquina sintética", SerialNumber = "HTTP-TEST", CompanyId = companyA });
            setup.Products.Add(new FullProduct { Id = "http-product", Name = "Produto sintético", CompanyId = companyA });
            setup.MachineProductSlots.Add(new MachineProductSlot { CompanyId = companyA, MachineId = "http-machine", ItemNumber = 1, ProductId = "http-product", Capacity = 20, CurrentStock = 2 });
            await setup.SaveChangesAsync();
        }

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        builder.Services.AddDbContext<AppDbContext>(db => db.UseSqlite(connection));
        builder.Services.AddSingleton<IAuditLogService, NoOpAudit>();
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddControllers().AddApplicationPart(typeof(InventoryController).Assembly);
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();

        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            var receiptPath = $"/api/inventory/movements/by-request/{requestId:D}";
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(receiptPath)).StatusCode);

            SetIdentity(companyA, userA, "Admin");
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(receiptPath)).StatusCode);
            var request = new { quantity = 3, reason, requestId };
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/inventory/slots/http-machine/1/restock", request)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/inventory/slots/http-machine/1/restock", request)).StatusCode);
            using var receiptResponse = await client.GetAsync(receiptPath);
            Assert.Equal(HttpStatusCode.OK, receiptResponse.StatusCode);
            using var receipt = JsonDocument.Parse(await receiptResponse.Content.ReadAsStringAsync());
            Assert.Equal("http-machine", receipt.RootElement.GetProperty("machineId").GetString());
            Assert.Equal(1, receipt.RootElement.GetProperty("itemNumber").GetInt32());
            Assert.Equal("Restock", receipt.RootElement.GetProperty("movementType").GetString());
            Assert.Equal(3, receipt.RootElement.GetProperty("quantityDelta").GetInt32());
            Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reason))).ToLowerInvariant(), receipt.RootElement.GetProperty("reasonHash").GetString());
            Assert.False(receipt.RootElement.TryGetProperty("reason", out _));

            SetIdentity(companyA, Guid.NewGuid(), "Admin");
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(receiptPath)).StatusCode);
            SetIdentity(companyB, userA, "Admin");
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(receiptPath)).StatusCode);
            SetIdentity(companyA, userA, "Operator");
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(receiptPath)).StatusCode);

            SetIdentity(companyA, userA, "Admin");
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/inventory/slots/http-machine/1/restock", new { quantity = 4, reason, requestId })).StatusCode);
            await using var verification = new AppDbContext(options);
            Assert.Equal(5, (await verification.MachineProductSlots.AsNoTracking().SingleAsync()).CurrentStock);
            Assert.Equal(1, await verification.InventoryMovements.CountAsync());

            void SetIdentity(Guid company, Guid user, string role)
            {
                client.DefaultRequestHeaders.Remove("X-Test-Company");
                client.DefaultRequestHeaders.Remove("X-Test-User");
                client.DefaultRequestHeaders.Remove("X-Test-Role");
                client.DefaultRequestHeaders.Add("X-Test-Company", company.ToString("D"));
                client.DefaultRequestHeaders.Add("X-Test-User", user.ToString("D"));
                client.DefaultRequestHeaders.Add("X-Test-Role", role);
            }
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private sealed class HeaderAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Request.Headers["X-Test-Role"].ToString();
            if (string.IsNullOrEmpty(role)) return Task.FromResult(AuthenticateResult.NoResult());
            var claims = new[]
            {
                new Claim(ClaimTypes.Role, role),
                new Claim(ClaimTypes.NameIdentifier, Request.Headers["X-Test-User"].ToString()),
                new Claim("company_id", Request.Headers["X-Test-Company"].ToString()),
            };
            var identity = new ClaimsIdentity(claims, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    private sealed class NoOpAudit : IAuditLogService
    {
        public Task RecordAsync(string action, string resourceType, string? resourceId = null, Guid? companyId = null,
            object? metadata = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
