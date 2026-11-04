using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Data;

namespace VendingMachines.Api.Services;

public interface IPasswordResetEmailQueue
{
    bool TryQueue(Guid resetTokenId, string recipient, string token, DateTime expiresAt);
}

public sealed class PasswordResetEmailQueue : BackgroundService, IPasswordResetEmailQueue
{
    private readonly Channel<PasswordResetEmailJob> _jobs = Channel.CreateBounded<PasswordResetEmailJob>(
        new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PasswordResetEmailQueue> _logger;

    public PasswordResetEmailQueue(
        IServiceScopeFactory scopeFactory,
        ILogger<PasswordResetEmailQueue> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public bool TryQueue(Guid resetTokenId, string recipient, string token, DateTime expiresAt) =>
        _jobs.Writer.TryWrite(new PasswordResetEmailJob(resetTokenId, recipient, token, expiresAt));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var job in _jobs.Reader.ReadAllAsync(stoppingToken))
                await DispatchAsync(job, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Outstanding links remain unusable until requested again; no token is exposed or persisted in plaintext.
        }
    }

    private async Task DispatchAsync(PasswordResetEmailJob job, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTime.UtcNow;
            var tokenIsUsable = await db.PasswordResetTokens.AsNoTracking().AnyAsync(
                token => token.Id == job.ResetTokenId && token.UsedAt == null &&
                         token.RevokedAt == null && token.ExpiresAt > now,
                cancellationToken);
            if (!tokenIsUsable)
                return;

            var sender = scope.ServiceProvider.GetRequiredService<IAccountEmailSender>();
            var sent = await sender.SendPasswordResetAsync(
                job.Recipient, job.Token, job.ExpiresAt, cancellationToken);
            if (!sent)
                await TryRevokeAsync(job.ResetTokenId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Falha ao processar uma solicitação de recuperação de senha ({FailureType}).",
                exception.GetType().Name);
            await TryRevokeAsync(job.ResetTokenId, cancellationToken);
        }
    }

    private async Task TryRevokeAsync(Guid resetTokenId, CancellationToken cancellationToken)
    {
        try
        {
            await RevokeAsync(resetTokenId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Não foi possível revogar um token após falha no envio ({FailureType}).",
                exception.GetType().Name);
        }
    }

    private async Task RevokeAsync(Guid resetTokenId, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;
        await db.PasswordResetTokens
            .Where(token => token.Id == resetTokenId && token.UsedAt == null && token.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(token => token.RevokedAt, now), cancellationToken);
    }

    private sealed record PasswordResetEmailJob(
        Guid ResetTokenId,
        string Recipient,
        string Token,
        DateTime ExpiresAt);
}
