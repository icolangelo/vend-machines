using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class PostgresRefreshSessionConcurrencyTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LogoutAndRefreshInEitherLockOrderLeaveNoActiveRefreshFamily(bool logoutLocksFirst)
    {
        var configuredConnection = Environment.GetEnvironmentVariable("VENDMACHINES_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(configuredConnection))
        {
            if (string.Equals(Environment.GetEnvironmentVariable("VENDMACHINES_REQUIRE_TEST_POSTGRES"), "true", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("O teste concorrente de logout e refresh exige VENDMACHINES_TEST_POSTGRES no CI.");
            return;
        }

        var databaseName = $"refresh_logout_race_{Guid.NewGuid():N}";
        var adminConnection = new NpgsqlConnectionStringBuilder(configuredConnection)
        {
            Database = "postgres",
            Pooling = false
        }.ConnectionString;
        var testConnection = new NpgsqlConnectionStringBuilder(configuredConnection)
        {
            Database = databaseName,
            Pooling = false
        }.ConnectionString;

        await using (var admin = new NpgsqlConnection(adminConnection))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin);
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(testConnection).Options;
            var email = $"refresh-race-{Guid.NewGuid():N}@example.test";
            var user = new User
            {
                Id = Guid.NewGuid(),
                Name = "Usuário sintético",
                Email = email,
                NormalizedEmail = email,
                PasswordHash = "synthetic-only",
                Role = AppRoles.Admin,
                IsActive = true,
                Cpf = Guid.NewGuid().ToString("N"),
                AcceptedPrivacyPolicy = true,
                CreatedAt = DateTime.UtcNow
            };
            await using (var setup = new AppDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                setup.Users.Add(user);
                await setup.SaveChangesAsync();
            }

            var authOptions = Options.Create(new AuthOptions { RefreshTokenDays = 30, RefreshReuseGraceSeconds = 10 });
            IssuedRefreshSession issued;
            Guid originalSessionId;
            await using (var issueDb = new AppDbContext(options))
            {
                var issuer = CreateService(issueDb, authOptions);
                issued = await issuer.CreateAsync(user, null, null);
                originalSessionId = await issueDb.RefreshSessions.AsNoTracking().Select(x => x.Id).SingleAsync();
            }

            await using var lockConnection = new NpgsqlConnection(testConnection);
            await lockConnection.OpenAsync();
            await using var lockTransaction = await lockConnection.BeginTransactionAsync();
            await using (var lockRow = new NpgsqlCommand(
                "SELECT \"Id\" FROM \"RefreshSessions\" WHERE \"Id\" = @id FOR UPDATE",
                lockConnection,
                lockTransaction))
            {
                lockRow.Parameters.AddWithValue("id", originalSessionId);
                Assert.NotNull(await lockRow.ExecuteScalarAsync());
            }

            async Task<RefreshRotationResult> RotateAsync()
            {
                await using var db = new AppDbContext(options);
                return await CreateService(db, authOptions).RotateAsync(issued.Token, null, null);
            }

            async Task RevokeAsync()
            {
                await using var db = new AppDbContext(options);
                await CreateService(db, authOptions).RevokeAsync(issued.Token);
            }

            var first = logoutLocksFirst ? RevokeAsync() : RotateAsync();
            await WaitForRefreshUpdateWaitersAsync(testConnection, expected: 1);
            var second = logoutLocksFirst ? RotateAsync() : RevokeAsync();
            await WaitForRefreshUpdateWaitersAsync(testConnection, expected: 2);

            await lockTransaction.CommitAsync();
            var rotated = logoutLocksFirst
                ? await CompleteRotationAsync(first, second)
                : await CompleteRotationAsync(second, first);

            await using var verification = new AppDbContext(options);
            Assert.Equal(0, await verification.RefreshSessions.CountAsync(session => session.RevokedAt == null));
            if (rotated.Status == RefreshRotationStatus.Succeeded)
            {
                Assert.NotNull(rotated.Token);
                Assert.Equal(RefreshRotationStatus.Reused,
                    (await CreateService(verification, authOptions).RotateAsync(rotated.Token!, null, null)).Status);
            }
            else
            {
                Assert.Equal(RefreshRotationStatus.Reused, rotated.Status);
                Assert.Null(rotated.Token);
            }
        }
        finally
        {
            await using var admin = new NpgsqlConnection(adminConnection);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static RefreshSessionService CreateService(AppDbContext db, IOptions<AuthOptions> options)
    {
        return new RefreshSessionService(db, options, TimeProvider.System, NullLogger<RefreshSessionService>.Instance);
    }

    private static async Task<RefreshRotationResult> CompleteRotationAsync(Task first, Task second)
    {
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(15));
        var rotation = first as Task<RefreshRotationResult> ?? second as Task<RefreshRotationResult>;
        return rotation?.Result ?? throw new InvalidOperationException("A corrida não iniciou a renovação.");
    }

    private static async Task WaitForRefreshUpdateWaitersAsync(string connectionString, int expected)
    {
        await using var observer = new NpgsqlConnection(connectionString);
        await observer.OpenAsync();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var waiting = 0;
        while (DateTime.UtcNow < deadline && waiting < expected)
        {
            await using var command = new NpgsqlCommand("""
                SELECT COUNT(*)
                FROM pg_stat_activity
                WHERE datname = current_database()
                  AND wait_event_type = 'Lock'
                  AND query ILIKE '%UPDATE%RefreshSessions%'
                """, observer);
            waiting = Convert.ToInt32(await command.ExecuteScalarAsync());
            if (waiting < expected) await Task.Delay(25);
        }

        Assert.True(waiting >= expected, $"Esperava {expected} operação(ões) aguardando lock em RefreshSessions, encontrei {waiting}.");
    }
}
