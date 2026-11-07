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

        Response.ContentType = "text/csv; charset=utf-8";
        Response.Headers["Content-Disposition"] = $"attachment; filename=vendas-{DateOnly.FromDateTime(start):yyyyMMdd}-{DateOnly.FromDateTime(endExclusive.AddDays(-1)):yyyyMMdd}.csv";
        Response.Headers["Cache-Control"] = "no-store";

        await using var writer = new StreamWriter(Response.Body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 4096, leaveOpen: true);
        await writer.WriteAsync("Id venda;ID pagamento Mercado Pago;Data UTC;Maquina;Cliente;Localizacao;Compartimento;Produto;Quantidade;Valor bruto;Estorno;Receita apos estornos antes da taxa;Taxa plataforma;Liquido original apos taxa;Situacao financeira\r\n".AsMemory(), cancellationToken);

        var asOfUtc = DateTime.UtcNow;
        var sales =
            from sale in db.SaleRecords.AsNoTracking()
            where sale.CompanyId == companyId && sale.SoldAt >= start && sale.SoldAt < endExclusive && sale.SoldAt <= asOfUtc
            join paymentCandidate in db.PaymentTransactions.AsNoTracking().Where(transaction => transaction.CompanyId == companyId)
                on sale.TransactionId equals paymentCandidate.Id into paymentMatches
            from payment in paymentMatches.DefaultIfEmpty()
            join machineCandidate in db.Machines.AsNoTracking().Where(machine => machine.CompanyId == companyId)
                on sale.MachineId equals machineCandidate.Id into machineMatches
            from machine in machineMatches.DefaultIfEmpty()
            join locationCandidate in db.Locations.AsNoTracking().Where(location => location.CompanyId == companyId)
                on machine.LocationId equals (Guid?)locationCandidate.Id into locationMatches
            from location in locationMatches.DefaultIfEmpty()
            select new { sale, payment, machine, location };

        if (!string.IsNullOrWhiteSpace(machineId))
            sales = sales.Where(row => row.sale.MachineId == machineId.Trim());
        if (locationId.HasValue)
            sales = sales.Where(row => row.sale.LocationIdSnapshot == locationId.Value ||
                (row.sale.LocationIdSnapshot == null && row.machine != null && row.machine.LocationId == locationId.Value));
        if (!string.IsNullOrWhiteSpace(productId))
            sales = sales.Where(row => row.sale.ProductId == productId.Trim());

        var rowsWritten = 0;
        await foreach (var row in sales.OrderByDescending(row => row.sale.SoldAt).ThenByDescending(row => row.sale.Id)
            .AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            var financialStatus = row.sale.FinancialStatus;
            decimal? netRevenue = financialStatus is "ReconciliationRequired" or "Disputed" or "Unpaid"
                ? null
                : row.sale.GrossAmount - row.sale.RefundedAmount;
            var cells = new[]
            {
                Csv(row.sale.Id.ToString()),
                CsvAsText(row.payment?.MercadoPagoPaymentId ?? string.Empty),
                Csv(row.sale.SoldAt.ToString("O", CultureInfo.InvariantCulture)),
                Csv(row.machine?.Name ?? "Máquina removida"),
                Csv(row.sale.LocationNameSnapshot ?? row.machine?.ClientName ?? string.Empty),
                Csv(row.sale.LocationNameSnapshot ?? row.location?.Name ?? row.machine?.Location ?? string.Empty),
                row.sale.ItemNumber.ToString(CultureInfo.InvariantCulture),
                Csv(row.sale.ProductNameSnapshot ?? "Produto sem mapeamento"),
                row.sale.Quantity.ToString(CultureInfo.InvariantCulture),
                Decimal(row.sale.GrossAmount),
                Decimal(row.sale.RefundedAmount),
                netRevenue.HasValue
                    ? Decimal(netRevenue.Value)
                    : Csv(financialStatus switch
                    {
                        "Disputed" => "Em contestação",
                        "Unpaid" => "Sem pagamento confirmado",
                        _ => "Pendente de conciliação"
                    }),
                Decimal(row.sale.ApplicationFee),
                Decimal(row.sale.NetAmount),
                Csv(FinancialStatusLabel(financialStatus))
            };
            await writer.WriteAsync($"{string.Join(";", cells)}\r\n".AsMemory(), cancellationToken);
            if (++rowsWritten % 128 == 0)
                await writer.FlushAsync(cancellationToken);
        }
        await writer.FlushAsync(cancellationToken);
        return new EmptyResult();
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
        var asOfUtc = DateTime.UtcNow;
        var query = db.SaleRecords.AsNoTracking()
            .Where(sale => sale.CompanyId == companyId && sale.SoldAt >= start &&
                           sale.SoldAt < endExclusive && sale.SoldAt <= asOfUtc);
        if (!string.IsNullOrWhiteSpace(machineId))
            query = query.Where(sale => sale.MachineId == machineId.Trim());
        if (locationId.HasValue)
            query = query.Where(sale => sale.LocationIdSnapshot == locationId.Value ||
                (sale.LocationIdSnapshot == null && db.Machines.Any(machine =>
                    machine.Id == sale.MachineId && machine.CompanyId == companyId && machine.LocationId == locationId.Value)));
        if (!string.IsNullOrWhiteSpace(productId))
            query = query.Where(sale => sale.ProductId == productId.Trim());

        query = query.OrderByDescending(sale => sale.SoldAt)
            .ThenByDescending(sale => sale.Id);
        var total = await query.CountAsync(cancellationToken);
        var sales = await query.Take(MaximumRows).ToListAsync(cancellationToken);
        var machineIds = sales.Select(sale => sale.MachineId).Distinct().ToArray();
        var transactionIds = sales.Select(sale => sale.TransactionId).Distinct().ToArray();
        var providerPaymentIds = await db.PaymentTransactions.AsNoTracking()
            .Where(transaction => transaction.CompanyId == companyId && transactionIds.Contains(transaction.Id))
            .ToDictionaryAsync(transaction => transaction.Id, transaction => transaction.MercadoPagoPaymentId, cancellationToken);
        var machines = await db.Machines.AsNoTracking()
            .Where(machine => machine.CompanyId == companyId && machineIds.Contains(machine.Id))
            .ToDictionaryAsync(machine => machine.Id, cancellationToken);
        var locationIds = sales.Select(sale => sale.LocationIdSnapshot)
            .Where(locationId => locationId.HasValue)
            .Select(locationId => locationId!.Value)
            .Concat(machines.Values.Where(machine => machine.LocationId.HasValue).Select(machine => machine.LocationId!.Value))
            .Distinct().ToArray();
        var locations = await db.Locations.AsNoTracking()
            .Where(location => location.CompanyId == companyId && locationIds.Contains(location.Id))
            .ToDictionaryAsync(location => location.Id, cancellationToken);
        var items = sales.Select(sale =>
        {
            machines.TryGetValue(sale.MachineId, out var machine);
            var locationName = sale.LocationNameSnapshot ?? machine?.Location ?? string.Empty;
            if (sale.LocationNameSnapshot == null && machine?.LocationId is Guid locationId && locations.TryGetValue(locationId, out var location))
                locationName = location.Name;
            providerPaymentIds.TryGetValue(sale.TransactionId, out var providerPaymentId);
            return new SalesReportRow(
                sale.Id,
                providerPaymentId,
                sale.SoldAt,
                sale.MachineId,
                machine?.Name ?? "Máquina removida",
                sale.LocationNameSnapshot ?? machine?.ClientName ?? string.Empty,
                locationName,
                sale.ItemNumber,
                sale.ProductNameSnapshot ?? "Produto sem mapeamento",
                sale.Quantity,
                sale.GrossAmount,
                sale.RefundedAmount,
                sale.FinancialStatus is "ReconciliationRequired" or "Disputed" or "Unpaid"
                    ? null
                    : sale.GrossAmount - sale.RefundedAmount,
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
        if (endDate == DateOnly.MaxValue)
        {
            start = endExclusive = default;
            error = "A data final informada não é válida para este relatório.";
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

    private static string CsvAsText(string value) =>
        value.Length == 0 ? Csv(value) : Csv($"'{value}");

    private static string Decimal(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string FinancialStatusLabel(string status) => status switch
    {
        "Settled" => "Liquidada",
        "PartiallyRefunded" => "Parcialmente estornada",
        "Refunded" => "Estornada",
        "Disputed" => "Em contestação",
        "Chargeback" => "Contestada pelo emissor",
        "ReconciliationRequired" => "Conciliação necessária",
        "Unpaid" => "Sem pagamento confirmado",
        _ => "Desconhecida"
    };

    private sealed record SalesReportResult(List<SalesReportRow> Items, int Total, bool Truncated);
    private sealed record SalesReportRow(Guid Id, string? MercadoPagoPaymentId, DateTime SoldAt, string MachineId, string MachineName, string ClientName, string Location,
        int ItemNumber, string ProductName, int Quantity, decimal GrossAmount, decimal RefundedAmount,
        decimal? NetRevenue, decimal ApplicationFee, decimal NetAfterPlatformFee, string FinancialStatus);
}
