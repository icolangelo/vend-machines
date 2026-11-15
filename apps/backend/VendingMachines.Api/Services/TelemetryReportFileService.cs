using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;

namespace VendingMachines.Api.Services;

public sealed class TelemetryReportFileService(AppDbContext db)
{
    private const int MaximumParts = 4096;
    private const int MaximumReportBytes = 10 * 1024 * 1024;
    private static readonly TimeSpan IncompleteTransferTimeout = TimeSpan.FromMinutes(2);
    private static readonly Regex SafeFileName = new("^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}\\.txt$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<bool> HasActiveRequestAsync(Guid companyId, string machineId, CancellationToken cancellationToken) =>
        await db.TelemetryReportTransferRequests.AsNoTracking().AnyAsync(request =>
            request.CompanyId == companyId && request.MachineId == machineId &&
            (request.Status == "waiting_session" || request.Status == "requesting" || request.Status == "receiving"),
            cancellationToken);

    public async Task<TelemetryReportTransferRequest> ReserveAsync(
        Guid requestId, Guid companyId, string machineId, Guid? userId, CancellationToken cancellationToken)
    {
        var request = new TelemetryReportTransferRequest
        {
            Id = requestId,
            CompanyId = companyId,
            MachineId = machineId,
            UserId = userId,
            Status = "waiting_session",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(3)
        };
        db.TelemetryReportTransferRequests.Add(request);
        await db.SaveChangesAsync(cancellationToken);
        return request;
    }

    public async Task SetRequestStateAsync(
        Guid requestId, string status, string? error, DateTime? expiresAt, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await db.TelemetryReportTransferRequests
            .Where(request => request.Id == requestId &&
                (request.Status == "waiting_session" || request.Status == "requesting" || request.Status == "receiving"))
            .ExecuteUpdateAsync(update => update
                .SetProperty(request => request.Status, status)
                .SetProperty(request => request.Error, error)
                .SetProperty(request => request.UpdatedAt, now)
                .SetProperty(request => request.ExpiresAt, expiresAt ?? now), cancellationToken);
    }

    public async Task<bool> TryStoreChunkAsync(InboundDeviceMessage message, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(message.DataJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("type", out var typeElement) ||
            typeElement.ValueKind != JsonValueKind.String ||
            !string.Equals(typeElement.GetString(), "report_file", StringComparison.OrdinalIgnoreCase))
            return false;

        var fileName = root.TryGetProperty("filename", out var fileNameElement) && fileNameElement.ValueKind == JsonValueKind.String
            ? fileNameElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(fileName) || !SafeFileName.IsMatch(fileName))
            throw new InvalidDataException("Invalid report filename.");

        if (!root.TryGetProperty("part", out var partElement) || !partElement.TryGetInt32(out var part) || part < 0 || part >= MaximumParts)
            throw new InvalidDataException("Invalid report part number.");

        if (!root.TryGetProperty("data", out var dataElement) || dataElement.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Invalid report chunk data.");
        var data = dataElement.GetString() ?? string.Empty;
        if (!root.TryGetProperty("eof", out var eofElement) || !TryReadEndOfFile(eofElement, out var isEndOfFile))
            throw new InvalidDataException("Invalid report end marker.");

        var byteLength = Encoding.UTF8.GetByteCount(data);
        if (byteLength > 60 * 1024)
            throw new InvalidDataException("Report chunk exceeds the allowed size.");

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await AcquireFileLockAsync(message.CompanyId, message.MachineId, fileName, cancellationToken);

        var existingValidation = await db.TelemetryReportFileValidations.AsNoTracking()
            .AnyAsync(validation => validation.CompanyId == message.CompanyId &&
                                    validation.MachineId == message.MachineId &&
                                    validation.FileName == fileName,
                cancellationToken);
        if (existingValidation)
        {
            var now = DateTime.UtcNow;
            await db.TelemetryReportTransferRequests
                .Where(request => request.CompanyId == message.CompanyId && request.MachineId == message.MachineId &&
                                  request.Status == "receiving" && request.FileName == fileName)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(request => request.Status, "completed")
                    .SetProperty(request => request.Error, (string?)null)
                    .SetProperty(request => request.UpdatedAt, now)
                    .SetProperty(request => request.ExpiresAt, now), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }

        var existing = await db.TelemetryReportFileChunks.AsNoTracking()
            .FirstOrDefaultAsync(chunk => chunk.CompanyId == message.CompanyId &&
                                          chunk.MachineId == message.MachineId &&
                                          chunk.FileName == fileName &&
                                          chunk.Part == part,
                cancellationToken);
        if (existing is not null)
        {
            if (existing.Data == data && existing.IsEndOfFile == isEndOfFile)
            {
                var now = DateTime.UtcNow;
                await db.TelemetryReportTransferRequests
                    .Where(request => request.CompanyId == message.CompanyId && request.MachineId == message.MachineId &&
                                      request.Status == "receiving" && (request.FileName == null || request.FileName == fileName))
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(request => request.FileName, fileName)
                        .SetProperty(request => request.UpdatedAt, now)
                        .SetProperty(request => request.ExpiresAt, now.Add(IncompleteTransferTimeout)), cancellationToken);
                await FinalizeIfReadyAsync(message.CompanyId, message.MachineId, fileName, cancellationToken);
                if (await db.TelemetryReportFileValidations.AsNoTracking().AnyAsync(validation =>
                        validation.CompanyId == message.CompanyId && validation.MachineId == message.MachineId &&
                        validation.FileName == fileName && validation.TransferStatus == "completed", cancellationToken))
                {
                    await db.TelemetryReportTransferRequests
                        .Where(request => request.CompanyId == message.CompanyId && request.MachineId == message.MachineId &&
                                          request.Status == "receiving" && request.FileName == fileName)
                        .ExecuteUpdateAsync(update => update
                            .SetProperty(request => request.Status, "completed")
                            .SetProperty(request => request.Error, (string?)null)
                            .SetProperty(request => request.UpdatedAt, now)
                            .SetProperty(request => request.ExpiresAt, now), cancellationToken);
                }
                await transaction.CommitAsync(cancellationToken);
                return true;
            }
            throw new InvalidDataException("Conflicting duplicate report chunk.");
        }

        var currentBytes = await db.TelemetryReportFileChunks
            .Where(chunk => chunk.CompanyId == message.CompanyId &&
                            chunk.MachineId == message.MachineId &&
                            chunk.FileName == fileName)
            .Select(chunk => (int?)chunk.ByteLength)
            .SumAsync(cancellationToken) ?? 0;
        if (currentBytes + byteLength > MaximumReportBytes)
            throw new InvalidDataException("Report file exceeds the allowed size.");

        db.TelemetryReportFileChunks.Add(new TelemetryReportFileChunk
        {
            CompanyId = message.CompanyId,
            MachineId = message.MachineId,
            FileName = fileName,
            Part = part,
            Data = data,
            ByteLength = byteLength,
            IsEndOfFile = isEndOfFile,
            MessageId = message.MessageId,
            ReceivedAt = message.ReceivedAt
        });
        var transferDeadline = DateTime.UtcNow.Add(IncompleteTransferTimeout);
        await db.TelemetryReportTransferRequests
            .Where(request => request.CompanyId == message.CompanyId && request.MachineId == message.MachineId &&
                              request.Status == "receiving" && (request.FileName == null || request.FileName == fileName))
            .ExecuteUpdateAsync(update => update
                .SetProperty(request => request.FileName, fileName)
                .SetProperty(request => request.UpdatedAt, DateTime.UtcNow)
                .SetProperty(request => request.ExpiresAt, transferDeadline), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await FinalizeIfReadyAsync(message.CompanyId, message.MachineId, fileName, cancellationToken);
        var completed = await db.TelemetryReportFileValidations.AsNoTracking().AnyAsync(validation =>
            validation.CompanyId == message.CompanyId && validation.MachineId == message.MachineId &&
            validation.FileName == fileName && validation.TransferStatus == "completed", cancellationToken);
        if (completed)
        {
            var now = DateTime.UtcNow;
            await db.TelemetryReportTransferRequests
                .Where(request => request.CompanyId == message.CompanyId && request.MachineId == message.MachineId &&
                                  request.Status == "receiving" && request.FileName == fileName)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(request => request.Status, "completed")
                    .SetProperty(request => request.Error, (string?)null)
                    .SetProperty(request => request.UpdatedAt, now)
                    .SetProperty(request => request.ExpiresAt, now), cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<int> FailStaleTransfersAsync(CancellationToken cancellationToken)
    {
        var cutoff = DateTime.UtcNow - IncompleteTransferTimeout;
        var candidates = await db.TelemetryReportFileChunks.AsNoTracking()
            .Where(chunk => !db.TelemetryReportFileValidations.Any(validation =>
                validation.CompanyId == chunk.CompanyId && validation.MachineId == chunk.MachineId && validation.FileName == chunk.FileName))
            .GroupBy(chunk => new { chunk.CompanyId, chunk.MachineId, chunk.FileName })
            .Select(group => new
            {
                group.Key.CompanyId,
                group.Key.MachineId,
                group.Key.FileName,
                LastReceivedAt = group.Max(chunk => chunk.ReceivedAt)
            })
            .Where(file => file.LastReceivedAt < cutoff)
            .OrderBy(file => file.LastReceivedAt)
            .Take(100)
            .ToListAsync(cancellationToken);

        var failed = 0;
        foreach (var candidate in candidates)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await AcquireFileLockAsync(candidate.CompanyId, candidate.MachineId, candidate.FileName, cancellationToken);

            if (await db.TelemetryReportFileValidations.AnyAsync(validation =>
                    validation.CompanyId == candidate.CompanyId && validation.MachineId == candidate.MachineId &&
                    validation.FileName == candidate.FileName, cancellationToken))
            {
                await transaction.CommitAsync(cancellationToken);
                continue;
            }

            var chunks = await db.TelemetryReportFileChunks.AsNoTracking()
                .Where(chunk => chunk.CompanyId == candidate.CompanyId && chunk.MachineId == candidate.MachineId &&
                                chunk.FileName == candidate.FileName)
                .ToListAsync(cancellationToken);
            if (chunks.Count == 0 || chunks.Max(chunk => chunk.ReceivedAt) >= cutoff)
            {
                await transaction.CommitAsync(cancellationToken);
                continue;
            }

            db.TelemetryReportFileValidations.Add(new TelemetryReportFileValidation
            {
                CompanyId = candidate.CompanyId,
                MachineId = candidate.MachineId,
                FileName = candidate.FileName,
                ValidatedAt = DateTime.UtcNow,
                PartCount = chunks.Count,
                SizeBytes = chunks.Sum(chunk => (long)chunk.ByteLength),
                TransferStatus = "failed",
                IsCrcValid = false,
                ValidationError = "Tempo esgotado: a transferência parou antes de receber eof: 1."
            });
            await db.SaveChangesAsync(cancellationToken);
            await db.TelemetryReportFileChunks
                .Where(chunk => chunk.CompanyId == candidate.CompanyId && chunk.MachineId == candidate.MachineId &&
                                chunk.FileName == candidate.FileName)
                .ExecuteDeleteAsync(cancellationToken);
            var now = DateTime.UtcNow;
            await db.TelemetryReportTransferRequests
                .Where(request => request.CompanyId == candidate.CompanyId && request.MachineId == candidate.MachineId &&
                                  request.FileName == candidate.FileName && request.Status == "receiving")
                .ExecuteUpdateAsync(update => update
                    .SetProperty(request => request.Status, "failed")
                    .SetProperty(request => request.Error, "Tempo esgotado: a transferência parou antes de receber eof: 1.")
                    .SetProperty(request => request.UpdatedAt, now)
                    .SetProperty(request => request.ExpiresAt, now), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            failed++;
        }

        var expiredRequests = await db.TelemetryReportTransferRequests
            .Where(request => (request.Status == "waiting_session" || request.Status == "requesting" || request.Status == "receiving") &&
                              request.ExpiresAt <= DateTime.UtcNow)
            .OrderBy(request => request.ExpiresAt)
            .Take(100)
            .ToListAsync(cancellationToken);
        foreach (var request in expiredRequests)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var current = await db.TelemetryReportTransferRequests.FirstOrDefaultAsync(item =>
                item.Id == request.Id &&
                (item.Status == "waiting_session" || item.Status == "requesting" || item.Status == "receiving") &&
                item.ExpiresAt <= DateTime.UtcNow, cancellationToken);
            if (current is null)
            {
                await transaction.CommitAsync(cancellationToken);
                continue;
            }

            const string timeoutError = "Tempo esgotado: a transferência do relatório não foi concluída.";
            if (!string.IsNullOrWhiteSpace(current.FileName))
            {
                var hasValidation = await db.TelemetryReportFileValidations.AnyAsync(item =>
                    item.CompanyId == current.CompanyId && item.MachineId == current.MachineId &&
                    item.FileName == current.FileName, cancellationToken);
                if (!hasValidation)
                {
                    var chunks = await db.TelemetryReportFileChunks.AsNoTracking()
                        .Where(item => item.CompanyId == current.CompanyId && item.MachineId == current.MachineId &&
                                       item.FileName == current.FileName)
                        .ToListAsync(cancellationToken);
                    db.TelemetryReportFileValidations.Add(new TelemetryReportFileValidation
                    {
                        CompanyId = current.CompanyId,
                        MachineId = current.MachineId,
                        FileName = current.FileName,
                        ValidatedAt = DateTime.UtcNow,
                        PartCount = chunks.Count,
                        SizeBytes = chunks.Sum(item => (long)item.ByteLength),
                        TransferStatus = "failed",
                        IsCrcValid = false,
                        ValidationError = timeoutError
                    });
                    await db.SaveChangesAsync(cancellationToken);
                    await db.TelemetryReportFileChunks
                        .Where(item => item.CompanyId == current.CompanyId && item.MachineId == current.MachineId &&
                                       item.FileName == current.FileName)
                        .ExecuteDeleteAsync(cancellationToken);
                }
            }
            current.Status = "failed";
            current.Error ??= timeoutError;
            current.UpdatedAt = DateTime.UtcNow;
            current.ExpiresAt = current.UpdatedAt;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            failed++;
        }

        return failed;
    }

    private async Task FinalizeIfReadyAsync(Guid companyId, string machineId, string fileName, CancellationToken cancellationToken)
    {
        if (await db.TelemetryReportFileValidations.AsNoTracking().AnyAsync(validation =>
                validation.CompanyId == companyId && validation.MachineId == machineId && validation.FileName == fileName,
                cancellationToken))
            return;

        var chunks = await db.TelemetryReportFileChunks.AsNoTracking()
            .Where(chunk => chunk.CompanyId == companyId && chunk.MachineId == machineId && chunk.FileName == fileName)
            .OrderBy(chunk => chunk.Part)
            .ToListAsync(cancellationToken);
        if (chunks.Count == 0 || chunks.Count > MaximumParts || chunks[0].Part != 0 ||
            chunks[^1].Part != chunks.Count - 1 || chunks.Count(chunk => chunk.IsEndOfFile) != 1 ||
            !chunks[^1].IsEndOfFile)
            return;

        var content = Encoding.UTF8.GetBytes(string.Concat(chunks.Select(chunk => chunk.Data)));
        var crc = ValidateReportCrc(content);
        db.TelemetryReportFileValidations.Add(new TelemetryReportFileValidation
        {
            CompanyId = companyId,
            MachineId = machineId,
            FileName = fileName,
            ValidatedAt = DateTime.UtcNow,
            PartCount = chunks.Count,
            SizeBytes = content.LongLength,
            TransferStatus = "completed",
            IsCrcValid = crc.IsValid,
            CalculatedCrc = crc.CalculatedCrc,
            ReportedCrc = crc.ReportedCrc,
            ValidationError = crc.Error
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task AcquireFileLockAsync(Guid companyId, string machineId, string fileName, CancellationToken cancellationToken)
    {
        if (!db.Database.IsNpgsql()) return;
        var lockKey = $"{companyId:N}:{machineId}:{fileName}";
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))",
            cancellationToken);
    }

    private static CrcValidationResult ValidateReportCrc(byte[] content)
    {
        var blockStart = IndexOf(content, "ST*"u8, 0);
        if (blockStart < 0)
            return new CrcValidationResult(false, null, null, "Marcador ST* não encontrado.");

        var g85LineStart = IndexOf(content, "\r\nG85*"u8, blockStart);
        if (g85LineStart < 0)
            return new CrcValidationResult(false, null, null, "Marcador G85* não encontrado após o bloco ST*.");

        var blockEnd = g85LineStart + 2;
        var crcMarker = IndexOf(content, "G85*"u8, blockEnd);
        if (crcMarker < 0 || content.Length < crcMarker + 8)
            return new CrcValidationResult(false, null, null, "Valor CRC G85*XXXX ausente ou incompleto.");

        string reported;
        try
        {
            reported = Encoding.ASCII.GetString(content, crcMarker + 4, 4).ToUpperInvariant();
        }
        catch (ArgumentException)
        {
            return new CrcValidationResult(false, null, null, "Valor CRC não está em ASCII.");
        }

        if (!ushort.TryParse(reported, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var reportedValue))
            return new CrcValidationResult(false, null, reported, $"Valor CRC '{reported}' inválido.");

        var calculatedValue = CalculateCrc16(content.AsSpan(blockStart, blockEnd - blockStart));
        var calculated = calculatedValue.ToString("X4", System.Globalization.CultureInfo.InvariantCulture);
        var reversed = (ushort)(((calculatedValue >> 8) & 0xff) | ((calculatedValue & 0xff) << 8));
        var valid = calculatedValue == reportedValue || reversed == reportedValue;
        return new CrcValidationResult(
            valid,
            calculated,
            reported,
            valid ? null : "CRC calculado diferente do CRC informado.");
    }

    private static ushort CalculateCrc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (var originalByte in data)
        {
            var value = originalByte;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (ushort)(((value ^ crc) & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1);
                value >>= 1;
            }
        }
        return crc;
    }

    private static int IndexOf(ReadOnlySpan<byte> source, ReadOnlySpan<byte> value, int start)
    {
        if (start < 0 || start > source.Length - value.Length) return -1;
        var offset = source[start..].IndexOf(value);
        return offset < 0 ? -1 : start + offset;
    }

    private static bool TryReadEndOfFile(JsonElement element, out bool value)
    {
        if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = element.GetBoolean();
            return true;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number) && number is 0 or 1)
        {
            value = number == 1;
            return true;
        }

        value = false;
        return false;
    }

    private sealed record CrcValidationResult(bool IsValid, string? CalculatedCrc, string? ReportedCrc, string? Error);
}
