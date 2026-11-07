using System.Security.Claims;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;

namespace VendingMachines.Api.Services;

public interface IAuditLogService
{
    Task RecordAsync(
        string action,
        string resourceType,
        string? resourceId = null,
        Guid? companyId = null,
        object? metadata = null,
        CancellationToken cancellationToken = default);

    Task RecordRequiredAsync(
        string action,
        string resourceType,
        string? resourceId = null,
        Guid? companyId = null,
        object? metadata = null,
        CancellationToken cancellationToken = default) =>
        RecordAsync(action, resourceType, resourceId, companyId, metadata, cancellationToken);
}

public sealed class AuditLogService : IAuditLogService
{
    private readonly AppDbContext _db;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<AuditLogService> _logger;

    public AuditLogService(
        AppDbContext db,
        IHttpContextAccessor httpContextAccessor,
        ILogger<AuditLogService> logger)
    {
        _db = db;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public async Task RecordAsync(
        string action,
        string resourceType,
        string? resourceId = null,
        Guid? companyId = null,
        object? metadata = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await RecordCoreAsync(action, resourceType, resourceId, companyId, metadata, cancellationToken);
        }
        catch (Exception ex)
        {
            // Auditoria nunca deve derrubar login, telemetria ou uma operação já concluída.
            _logger.LogError(
                "Falha ao registrar auditoria para {Action}; tipo de erro {ErrorType}.",
                action,
                ex.GetType().Name);
        }
    }

    public Task RecordRequiredAsync(
        string action,
        string resourceType,
        string? resourceId = null,
        Guid? companyId = null,
        object? metadata = null,
        CancellationToken cancellationToken = default) =>
        RecordCoreAsync(action, resourceType, resourceId, companyId, metadata, cancellationToken);

    private async Task RecordCoreAsync(
        string action,
        string resourceType,
        string? resourceId,
        Guid? companyId,
        object? metadata,
        CancellationToken cancellationToken)
    {
        var context = _httpContextAccessor.HttpContext;
        var actorUserId = Guid.TryParse(
            context?.User.FindFirstValue(ClaimTypes.NameIdentifier),
            out var parsedUserId) ? parsedUserId : (Guid?)null;
        var effectiveCompanyId = companyId ?? (Guid.TryParse(
            context?.User.FindFirstValue("company_id"),
            out var parsedCompanyId) ? parsedCompanyId : (Guid?)null);

        _db.AuditLogs.Add(new AuditLog
        {
            CompanyId = effectiveCompanyId,
            ActorUserId = actorUserId,
            Action = action,
            ResourceType = resourceType,
            ResourceId = resourceId,
            IpAddress = context?.Connection.RemoteIpAddress?.ToString(),
            UserAgent = Truncate(context?.Request.Headers.UserAgent.ToString(), 512),
            MetadataJson = AuditMetadataRedactor.SerializeRedacted(metadata),
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(cancellationToken);
    }

    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrEmpty(value) ? value : value.Length <= maxLength ? value : value[..maxLength];
}
