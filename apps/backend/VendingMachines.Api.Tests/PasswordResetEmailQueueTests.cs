using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class PasswordResetEmailQueueTests
{
    [Fact]
    public async Task FailedDeliveryRevokesTheQueuedResetToken()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var user = new User
        {
            Id = Guid.NewGuid(), Name = "Operador", Email = "operator@example.com",
            NormalizedEmail = "operator@example.com", PasswordHash = "hash", IsActive = true
        };
        var rawToken = OneTimeTokens.Create();
        var reset = new PasswordResetToken
        {
            UserId = user.Id,
            User = user,
            CreatedByUserId = user.Id,
            TokenHash = OneTimeTokens.Hash(rawToken),
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1)
        };
        db.Users.Add(user);
        db.PasswordResetTokens.Add(reset);
        await db.SaveChangesAsync();

        var sender = new RecordingAccountEmailSender();
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(builder => builder.UseSqlite(connection));
        services.AddSingleton<IAccountEmailSender>(sender);
        await using var provider = services.BuildServiceProvider();
        var queue = new PasswordResetEmailQueue(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PasswordResetEmailQueue>.Instance);

        Assert.True(queue.TryQueue(reset.Id, user.Email, rawToken, reset.ExpiresAt));
        await queue.StartAsync(CancellationToken.None);
        try
        {
            var deliveredJob = await sender.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(user.Email, deliveredJob.Recipient);
            Assert.Equal(rawToken, deliveredJob.Token);

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                await db.Entry(reset).ReloadAsync();
                if (reset.RevokedAt.HasValue)
                    break;
                await Task.Delay(20);
            }

            Assert.NotNull(reset.RevokedAt);
        }
        finally
        {
            await queue.StopAsync(CancellationToken.None);
        }
    }

    private sealed class RecordingAccountEmailSender : IAccountEmailSender
    {
        public TaskCompletionSource<(string Recipient, string Token)> Attempted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> SendEmailVerificationAsync(string recipient, string token, DateTime expiresAt,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<bool> SendInvitationAsync(string recipient, string role, string token, DateTime expiresAt,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<bool> SendPasswordResetAsync(string recipient, string token, DateTime expiresAt,
            CancellationToken cancellationToken)
        {
            Attempted.TrySetResult((recipient, token));
            return Task.FromResult(false);
        }
    }
}
