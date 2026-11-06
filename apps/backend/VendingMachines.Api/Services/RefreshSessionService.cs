using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;

namespace VendingMachines.Api.Services;

public enum RefreshRotationStatus
{
    Succeeded,
    Invalid,
    Expired,
    Reused,
    Concurrent
}

public sealed record IssuedRefreshSession(string Token, DateTime ExpiresAt);

public sealed record RefreshRotationResult(
    RefreshRotationStatus Status,
    User? User = null,
    string? Token = null,
    DateTime? ExpiresAt = null);

public sealed class RefreshSessionService
{
    private readonly AppDbContext _context;
    private readonly AuthOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RefreshSessionService> _logger;

    public RefreshSessionService(
        AppDbContext context,
        IOptions<AuthOptions> options,
        TimeProvider timeProvider,
        ILogger<RefreshSessionService> logger)
    {
        _context = context;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<IssuedRefreshSession> CreateAsync(
        User user,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken = default)
    {
        var now = UtcNow();
        var expiresAt = now.AddDays(_options.RefreshTokenDays);
        var token = GenerateToken();

        _context.RefreshSessions.Add(new RefreshSession
        {
            UserId = user.Id,
            TokenHash = HashToken(token),
            CreatedAt = now,
            ExpiresAt = expiresAt,
            CreatedByIp = Truncate(ipAddress, 64),
            UserAgent = Truncate(userAgent, 512)
        });
        await _context.SaveChangesAsync(cancellationToken);

        return new IssuedRefreshSession(token, expiresAt);
    }

    public async Task<RefreshRotationResult> RotateAsync(
        string token,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken = default)
    {
        var now = UtcNow();
        var tokenHash = HashToken(token);
        var current = await FindByHashAsync(tokenHash, cancellationToken);

        if (current == null)
        {
            return new RefreshRotationResult(RefreshRotationStatus.Invalid);
        }

        if (current.RevokedAt.HasValue)
        {
            return await HandleRevokedSessionAsync(current, now, cancellationToken);
        }

        if (current.ExpiresAt <= now)
        {
            await RevokeSessionAsync(current.Id, now, cancellationToken);
            return new RefreshRotationResult(RefreshRotationStatus.Expired);
        }

        var replacementId = Guid.NewGuid();
        var replacementToken = GenerateToken();
        var replacement = new RefreshSession
        {
            Id = replacementId,
            UserId = current.UserId,
            FamilyId = current.FamilyId,
            TokenHash = HashToken(replacementToken),
            CreatedAt = now,
            ExpiresAt = current.ExpiresAt,
            CreatedByIp = Truncate(ipAddress, 64),
            UserAgent = Truncate(userAgent, 512)
        };

        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        var updated = await _context.RefreshSessions
            .Where(x => x.Id == current.Id && x.RevokedAt == null && x.ExpiresAt > now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.LastUsedAt, now)
                .SetProperty(x => x.RevokedAt, now)
                .SetProperty(x => x.ReplacedBySessionId, replacementId), cancellationToken);

        if (updated != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            var refreshed = await FindByHashAsync(tokenHash, cancellationToken);
            return refreshed == null
                ? new RefreshRotationResult(RefreshRotationStatus.Invalid)
                : await HandleRevokedSessionAsync(refreshed, now, cancellationToken);
        }

        _context.RefreshSessions.Add(replacement);
        await _context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var user = await _context.Users
            .Include(x => x.Company)
            .SingleOrDefaultAsync(x => x.Id == current.UserId, cancellationToken);

        if (user == null)
        {
            await RevokeFamilyAsync(current.FamilyId, now, cancellationToken);
            return new RefreshRotationResult(RefreshRotationStatus.Invalid);
        }

        return new RefreshRotationResult(
            RefreshRotationStatus.Succeeded,
            user,
            replacementToken,
            replacement.ExpiresAt);
    }

    public async Task RevokeAsync(string token, CancellationToken cancellationToken = default)
    {
        var now = UtcNow();
        var tokenHash = HashToken(token);
        await _context.RefreshSessions
            .Where(x => x.TokenHash == tokenHash && x.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.RevokedAt, now), cancellationToken);
    }

    private async Task<RefreshRotationResult> HandleRevokedSessionAsync(
        RefreshSession session,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var grace = TimeSpan.FromSeconds(_options.RefreshReuseGraceSeconds);
        if (session.ReplacedBySessionId.HasValue && session.RevokedAt >= now.Subtract(grace))
        {
            return new RefreshRotationResult(RefreshRotationStatus.Concurrent);
        }

        await RevokeFamilyAsync(session.FamilyId, now, cancellationToken);
        _logger.LogWarning(
            "Reutilização de refresh token detectada para usuário {UserId} e família {FamilyId}.",
            session.UserId,
            session.FamilyId);
        return new RefreshRotationResult(RefreshRotationStatus.Reused);
    }

    private Task<RefreshSession?> FindByHashAsync(string hash, CancellationToken cancellationToken)
    {
        return _context.RefreshSessions
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.TokenHash == hash, cancellationToken);
    }

    private Task<int> RevokeSessionAsync(Guid id, DateTime now, CancellationToken cancellationToken)
    {
        return _context.RefreshSessions
            .Where(x => x.Id == id && x.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.RevokedAt, now), cancellationToken);
    }

    private Task<int> RevokeFamilyAsync(Guid familyId, DateTime now, CancellationToken cancellationToken)
    {
        return _context.RefreshSessions
            .Where(x => x.FamilyId == familyId && x.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.RevokedAt, now), cancellationToken);
    }

    private DateTime UtcNow() => _timeProvider.GetUtcNow().UtcDateTime;

    private static string GenerateToken()
    {
        return Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(64));
    }

    private static string HashToken(string token)
    {
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
    }

    private static string? Truncate(string? value, int maxLength)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value[..Math.Min(value.Length, maxLength)];
    }
}
