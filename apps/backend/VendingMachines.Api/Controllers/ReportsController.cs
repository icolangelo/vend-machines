using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;

namespace VendingMachines.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/reports")]
public sealed class ReportsController(AppDbContext db) : BaseApiController
{
    private const int MaximumRows = 10_000;

    [HttpGet("sales")]
    public async Task<IActionResult> GetSales(
        [FromQuery] DateOnly? from = null,
        [FromQuery] DateOnly? to = null,
        [FromQuery] string? machineId = null,
        [FromQuery] Guid? locationId = null,
        [FromQuery] string? productId = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetEffectiveCompanyId(out var companyId))
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });
        if (!TryGetDateRange(from, to, out var start, out var endExclusive, out var error))
            return BadRequest(new { message = error });

        var report = await LoadSalesAsync(companyId, start, endExclusive, machineId, locationId, productId, cancellationToken);
        return Ok(new
        {
            from = DateOnly.FromDateTime(start),
            to = DateOnly.FromDateTime(endExclusive.AddDays(-1)),
            total = report.Total,
            truncated = report.Truncated,
            items = report.Items
        });
    }

    [HttpGet("sales/export")]
    public async Task<IActionResult> ExportSales(
        [FromQuery] DateOnly? from = null,
        [FromQuery] DateOnly? to = null,
        [FromQuery] string? machineId = null,
        [FromQuery] Guid? locationId = null,
        [FromQuery] string? productId = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetEffectiveCompanyId(out var companyId))
            return BadRequest(new { message = "O usuário não está associado a nenhuma empresa." });
        if (!TryGetDateRange(from, to, out var start, out var endExclusive, out var error))
            return BadRequest(new { message = error });

        var report = await LoadSalesAsync(companyId, start, endExclusive, machineId, locationId, productId, cancellationToken);
        var csv = new StringBuilder();
        csv.AppendLine("Id;Data UTC;Maquina;Cliente;Localizacao;Compartimento;Produto;Quantidade;Valor bruto;Estorno;Receita apos estornos antes da taxa;Taxa plataforma;Liquido original apos taxa;Situacao financeira");
        foreach (var row in report.Items)
        {
            csv.Append(Csv(row.Id.ToString()))
                .Append(';').Append(Csv(row.SoldAt.ToString("O", CultureInfo.InvariantCulture)))
                .Append(';').Append(Csv(row.MachineName))
                .Append(';').Append(Csv(row.ClientName))
                .Append(';').Append(Csv(row.Location))
                .Append(';').Append(row.ItemNumber.ToString(CultureInfo.InvariantCulture))
                .Append(';').Append(Csv(row.ProductName))
                .Append(';').Append(row.Quantity.ToString(CultureInfo.InvariantCulture))
                .Append(';').Append(Decimal(row.GrossAmount))
                .Append(';').Append(Decimal(row.RefundedAmount))
                .Append(';').Append(Decimal(row.NetRevenue))
                .Append(';').Append(Decimal(row.ApplicationFee))
                .Append(';').Append(Decimal(row.NetAfterPlatformFee))
                .Append(';').Append(Csv(FinancialStatusLabel(row.FinancialStatus)))
                .AppendLine();
        }

        var fileName = $"vendas-{DateOnly.FromDateTime(start):yyyyMMdd}-{DateOnly.FromDateTime(endExclusive.AddDays(-1)):yyyyMMdd}.csv";
        return File(new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(csv.ToString()), "text/csv; charset=utf-8", fileName);
    }

    private async Task<SalesReportResult> LoadSalesAsync(
        Guid companyId,
        DateTime start,
        DateTime endExclusive,
        string? machineId,
        Guid? locationId,
        string? productId,
        CancellationToken cancellationToken)
    {
        var query = db.SaleRecords.AsNoTracking()
            .Where(sale => sale.CompanyId == companyId && sale.SoldAt >= start && sale.SoldAt < endExclusive);
        if (!string.IsNullOrWhiteSpace(machineId))
            query = query.Where(sale => sale.MachineId == machineId.Trim());
        if (locationId.HasValue)
            query = query.Where(sale => db.Machines.Any(machine =>
                machine.Id == sale.MachineId && machine.CompanyId == companyId && machine.LocationId == locationId.Value));
        if (!string.IsNullOrWhiteSpace(productId))
            query = query.Where(sale => sale.ProductId == productId.Trim());

        query = query.OrderByDescending(sale => sale.SoldAt);
        var total = await query.CountAsync(cancellationToken);
        var sales = await query.Take(MaximumRows).ToListAsync(cancellationToken);
        var machineIds = sales.Select(sale => sale.MachineId).Distinct().ToArray();
        var machines = await db.Machines.AsNoTracking()
            .Where(machine => machine.CompanyId == companyId && machineIds.Contains(machine.Id))
            .ToDictionaryAsync(machine => machine.Id, cancellationToken);
        var locationIds = machines.Values.Where(machine => machine.LocationId.HasValue).Select(machine => machine.LocationId!.Value).Distinct().ToArray();
        var locations = await db.Locations.AsNoTracking()
            .Where(location => location.CompanyId == companyId && locationIds.Contains(location.Id))
            .ToDictionaryAsync(location => location.Id, cancellationToken);
        var items = sales.Select(sale =>
        {
            machines.TryGetValue(sale.MachineId, out var machine);
            var locationName = machine?.Location ?? string.Empty;
            if (machine?.LocationId is Guid locationId && locations.TryGetValue(locationId, out var location))
                locationName = location.Name;
            return new SalesReportRow(
                sale.Id,
                sale.SoldAt,
                sale.MachineId,
                machine?.Name ?? "Máquina removida",
                machine?.ClientName ?? string.Empty,
                locationName,
                sale.ItemNumber,
                sale.ProductNameSnapshot ?? "Produto sem mapeamento",
                sale.Quantity,
                sale.GrossAmount,
                sale.RefundedAmount,
                sale.GrossAmount - sale.RefundedAmount,
                sale.ApplicationFee,
                sale.NetAmount,
                sale.FinancialStatus);
        }).ToList();
        return new SalesReportResult(items, total, total > MaximumRows);
    }

    private static bool TryGetDateRange(DateOnly? from, DateOnly? to, out DateTime start, out DateTime endExclusive, out string error)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var startDate = from ?? today.AddDays(-29);
        var endDate = to ?? today;
        if (startDate > endDate)
        {
            start = endExclusive = default;
            error = "A data inicial deve ser anterior ou igual à data final.";
            return false;
        }
        if (endDate.DayNumber - startDate.DayNumber > 365)
        {
            start = endExclusive = default;
            error = "O período máximo do relatório é de 366 dias.";
            return false;
        }
        start = DateTime.SpecifyKind(startDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        endExclusive = DateTime.SpecifyKind(endDate.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        error = string.Empty;
        return true;
    }

    private static string Csv(string value)
    {
        var firstValueCharacter = value.TrimStart();
        var safe = value.Length > 0 &&
            ((value[0] is '\t' or '\r' or '\n') ||
             (firstValueCharacter.Length > 0 && (firstValueCharacter[0] is '=' or '+' or '-' or '@')))
                ? $"'{value}"
                : value;
        return $"\"{safe.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static string Decimal(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string FinancialStatusLabel(string status) => status switch
    {
        "Settled" => "Liquidada",
        "PartiallyRefunded" => "Parcialmente estornada",
        "Refunded" => "Estornada",
        "Disputed" => "Em contestação",
        "Chargeback" => "Contestada pelo emissor",
        "ReconciliationRequired" => "Conciliação necessária",
        _ => "Desconhecida"
    };

    private sealed record SalesReportResult(List<SalesReportRow> Items, int Total, bool Truncated);
    private sealed record SalesReportRow(Guid Id, DateTime SoldAt, string MachineId, string MachineName, string ClientName, string Location,
        int ItemNumber, string ProductName, int Quantity, decimal GrossAmount, decimal RefundedAmount,
        decimal NetRevenue, decimal ApplicationFee, decimal NetAfterPlatformFee, string FinancialStatus);
}
