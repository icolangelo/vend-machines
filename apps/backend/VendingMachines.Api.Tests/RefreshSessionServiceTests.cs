using System.IdentityModel.Tokens.Jwt;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class RefreshSessionServiceTests
{
    [Fact]
    public async Task RotateAsync_RotatesTokenAndRevokesPreviousSession()
    {
        await using var scenario = await RefreshScenario.CreateAsync();
        var issued = await scenario.Service.CreateAsync(scenario.User, "127.0.0.1", "test-agent");

        var result = await scenario.Service.RotateAsync(issued.Token, "127.0.0.1", "test-agent");

        Assert.Equal(RefreshRotationStatus.Succeeded, result.Status);
        Assert.NotNull(result.Token);
        Assert.NotEqual(issued.Token, result.Token);
        Assert.Equal(scenario.User.Id, result.User?.Id);

        var sessions = await scenario.Db.RefreshSessions.AsNoTracking().OrderBy(x => x.CreatedAt).ToListAsync();
        Assert.Equal(2, sessions.Count);
        Assert.NotNull(sessions[0].RevokedAt);
        Assert.Equal(sessions[1].Id, sessions[0].ReplacedBySessionId);
        Assert.Null(sessions[1].RevokedAt);
        Assert.DoesNotContain(sessions, x => x.TokenHash == issued.Token || x.TokenHash == result.Token);
    }

    [Fact]
    public async Task RotateAsync_RepeatedWithinGracePeriod_IsTreatedAsConcurrent()
    {
        await using var scenario = await RefreshScenario.CreateAsync();
        var issued = await scenario.Service.CreateAsync(scenario.User, null, null);
        Assert.Equal(
            RefreshRotationStatus.Succeeded,
            (await scenario.Service.RotateAsync(issued.Token, null, null)).Status);

        var repeated = await scenario.Service.RotateAsync(issued.Token, null, null);

        Assert.Equal(RefreshRotationStatus.Concurrent, repeated.Status);
        Assert.Equal(1, await scenario.Db.RefreshSessions.AsNoTracking().CountAsync(x => x.RevokedAt == null));
    }

    [Fact]
    public async Task RotateAsync_ReusedAfterGracePeriod_RevokesTokenFamily()
    {
        await using var scenario = await RefreshScenario.CreateAsync();
        var issued = await scenario.Service.CreateAsync(scenario.User, null, null);
        Assert.Equal(
            RefreshRotationStatus.Succeeded,
            (await scenario.Service.RotateAsync(issued.Token, null, null)).Status);
        scenario.Clock.Advance(TimeSpan.FromSeconds(11));

        var repeated = await scenario.Service.RotateAsync(issued.Token, null, null);

        Assert.Equal(RefreshRotationStatus.Reused, repeated.Status);
        Assert.Equal(0, await scenario.Db.RefreshSessions.AsNoTracking().CountAsync(x => x.RevokedAt == null));
    }

    [Fact]
    public void CreateAccessToken_UsesConfiguredShortLifetime()
    {
        var clock = new AdjustableTimeProvider(new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "UnitTestSigningKeyThatIsLongEnough2026!",
            ["Jwt:Issuer"] = "Tests",
            ["Jwt:Audience"] = "Tests"
        }).Build();
        var service = new AuthTokenService(
            configuration,
            Options.Create(new AuthOptions { AccessTokenMinutes = 15 }),
            clock);
        var user = new User { Name = "Teste", Email = "teste@example.com", Role = "Admin" };

        var result = service.CreateAccessToken(user);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(result.Token);

        Assert.Equal(clock.GetUtcNow().UtcDateTime.AddMinutes(15), result.ExpiresAt);
        Assert.Equal(result.ExpiresAt, jwt.ValidTo);
    }

    private sealed class RefreshScenario : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private RefreshScenario(
            SqliteConnection connection,
            AppDbContext db,
            AdjustableTimeProvider clock,
            User user,
            RefreshSessionService service)
        {
            _connection = connection;
            Db = db;
            Clock = clock;
            User = user;
            Service = service;
        }

        public AppDbContext Db { get; }
        public AdjustableTimeProvider Clock { get; }
        public User User { get; }
        public RefreshSessionService Service { get; }

        public static async Task<RefreshScenario> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
            var db = new AppDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var user = new User
            {
                Name = "Usuário Teste",
                Email = $"{Guid.NewGuid():N}@example.com",
                PasswordHash = "hash",
                Cpf = Guid.NewGuid().ToString("N")
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();

            var clock = new AdjustableTimeProvider(new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero));
            var service = new RefreshSessionService(
                db,
                Options.Create(new AuthOptions
                {
                    RefreshTokenDays = 30,
                    RefreshReuseGraceSeconds = 10
                }),
                clock,
                NullLogger<RefreshSessionService>.Instance);

            return new RefreshScenario(connection, db, clock, user, service);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    public sealed class AdjustableTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public AdjustableTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan amount)
        {
            _utcNow = _utcNow.Add(amount);
        }
    }
}
