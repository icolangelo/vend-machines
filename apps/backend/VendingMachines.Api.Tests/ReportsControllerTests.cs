using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Controllers;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using VendingMachines.Api.Services;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class ReportsControllerTests
{
    [Fact]
    public async Task SalesReportIsCompanyScopedAndCsvEscapesFormulaContent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var companyA = new Company { Id = Guid.NewGuid(), Name = "Empresa A", Cnpj = "11111111000111" };
        var companyB = new Company { Id = Guid.NewGuid(), Name = "Empresa B", Cnpj = "22222222000122" };
        var locationA = new Location { Id = Guid.NewGuid(), CompanyId = companyA.Id, Name = "Unidade A" };
        var productA = new FullProduct { Id = "report-product-a", Name = "Produto A", CompanyId = companyA.Id };
        var machineA = new Machine { Id = "report-machine-a", Name = "Máquina A", SerialNumber = "RA", Location = "Local A", ClientName = " \n=1+1", CompanyId = companyA.Id, LocationId = locationA.Id };
        var machineB = new Machine { Id = "report-machine-b", Name = "Máquina B", SerialNumber = "RB", Location = "Local B", CompanyId = companyB.Id };
        var sessionA = new MachineSession { Id = Guid.NewGuid(), CompanyId = companyA.Id, MachineId = machineA.Id, State = MachineSessionStates.Completed };
        var sessionB = new MachineSession { Id = Guid.NewGuid(), CompanyId = companyB.Id, MachineId = machineB.Id, State = MachineSessionStates.Completed };
        var transactionA = new PaymentTransaction { Id = Guid.NewGuid(), CompanyId = companyA.Id, MachineId = machineA.Id, Amount = 12m, Status = "Approved" };
        var transactionB = new PaymentTransaction { Id = Guid.NewGuid(), CompanyId = companyB.Id, MachineId = machineB.Id, Amount = 99m, Status = "Approved" };
        sessionA.TransactionId = transactionA.Id;
        sessionB.TransactionId = transactionB.Id;
        var soldAt = DateTime.UtcNow.Date.AddDays(-1).AddHours(10);
        var saleA = new SaleRecord
        {
            CompanyId = companyA.Id, MachineId = machineA.Id, SessionId = sessionA.Id, TransactionId = transactionA.Id,
            SoldAt = soldAt, ProductNameSnapshot = "=2+2", Quantity = 1, GrossAmount = 12m,
            RefundedAmount = 2m, ApplicationFee = 1m, NetAmount = 11m, ProductId = productA.Id,
            FinancialStatus = "ReconciliationRequired"
        };
        var saleB = new SaleRecord
        {
            CompanyId = companyB.Id, MachineId = machineB.Id, SessionId = sessionB.Id, TransactionId = transactionB.Id,
            SoldAt = soldAt, ProductNameSnapshot = "Privado", Quantity = 1, GrossAmount = 99m
        };
        db.Companies.AddRange(companyA, companyB);
        db.Locations.Add(locationA);
        db.Products.Add(productA);
        db.Machines.AddRange(machineA, machineB);
        db.MachineSessions.AddRange(sessionA, sessionB);
        db.PaymentTransactions.AddRange(transactionA, transactionB);
        db.SaleRecords.AddRange(saleA, saleB);
        await db.SaveChangesAsync();

        var controller = CreateController(db, companyA.Id);
        var jsonResult = Assert.IsType<OkObjectResult>(await controller.GetSales(DateOnly.FromDateTime(soldAt), DateOnly.FromDateTime(soldAt), cancellationToken: CancellationToken.None));
        var payload = JsonSerializer.SerializeToElement(jsonResult.Value);
        Assert.Equal(1, payload.GetProperty("total").GetInt32());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("items")[0].GetProperty("NetRevenue").ValueKind);
        Assert.Equal(1m, payload.GetProperty("items")[0].GetProperty("ApplicationFee").GetDecimal());
        Assert.Equal(11m, payload.GetProperty("items")[0].GetProperty("NetAfterPlatformFee").GetDecimal());

        var filteredResult = Assert.IsType<OkObjectResult>(await controller.GetSales(
            DateOnly.FromDateTime(soldAt), DateOnly.FromDateTime(soldAt),
            machineId: machineA.Id, locationId: locationA.Id, productId: productA.Id,
            cancellationToken: CancellationToken.None));
        var filteredPayload = JsonSerializer.SerializeToElement(filteredResult.Value);
        Assert.Equal(1, filteredPayload.GetProperty("total").GetInt32());

        var otherCompanyFilter = Assert.IsType<OkObjectResult>(await controller.GetSales(
            DateOnly.FromDateTime(soldAt), DateOnly.FromDateTime(soldAt),
            machineId: machineB.Id, cancellationToken: CancellationToken.None));
        Assert.Equal(0, JsonSerializer.SerializeToElement(otherCompanyFilter.Value).GetProperty("total").GetInt32());

        var forgedImpersonationController = CreateController(db, companyA.Id, AppRoles.Admin, companyB.Id);
        var forgedImpersonation = Assert.IsType<OkObjectResult>(await forgedImpersonationController.GetSales(
            DateOnly.FromDateTime(soldAt), DateOnly.FromDateTime(soldAt), cancellationToken: CancellationToken.None));
        Assert.Equal(1, JsonSerializer.SerializeToElement(forgedImpersonation.Value).GetProperty("total").GetInt32());

        var platformImpersonationController = CreateController(db, Guid.Empty, AppRoles.PlatformAdmin, companyB.Id);
        var platformImpersonation = Assert.IsType<OkObjectResult>(await platformImpersonationController.GetSales(
            DateOnly.FromDateTime(soldAt), DateOnly.FromDateTime(soldAt), cancellationToken: CancellationToken.None));
        Assert.Equal(1, JsonSerializer.SerializeToElement(platformImpersonation.Value).GetProperty("total").GetInt32());

        var csvStream = new MemoryStream();
        controller.ControllerContext.HttpContext.Response.Body = csvStream;
        Assert.IsType<EmptyResult>(await controller.ExportSales(
            DateOnly.FromDateTime(soldAt), DateOnly.FromDateTime(soldAt),
            machineId: machineA.Id, locationId: locationA.Id, productId: productA.Id,
            cancellationToken: CancellationToken.None));
        Assert.Equal("text/csv; charset=utf-8", controller.ControllerContext.HttpContext.Response.ContentType);
        Assert.Contains("attachment; filename=vendas-", controller.ControllerContext.HttpContext.Response.Headers.ContentDisposition.ToString());
        var csv = Encoding.UTF8.GetString(csvStream.ToArray());
        Assert.Contains("'=2+2", csv);
        Assert.Contains("\"' \n=1+1\"", csv);
        Assert.Contains("Liquido original apos taxa", csv);
        Assert.Contains("Pendente de conciliação", csv);
        Assert.Contains("11.00", csv);
        Assert.Contains("Conciliação necessária", csv);
        Assert.DoesNotContain("ReconciliationRequired", csv);
        Assert.DoesNotContain("Privado", csv);
    }

    [Fact]
    public async Task SalesReportRejectsMaximumDateInsteadOfFailingWhileBuildingExclusiveEnd()
    {
        var controller = CreateController(null!, Guid.NewGuid());

        var salesResult = Assert.IsType<BadRequestObjectResult>(await controller.GetSales(
            DateOnly.MaxValue, DateOnly.MaxValue, cancellationToken: CancellationToken.None));
        Assert.Equal("A data final informada não é válida para este relatório.",
            JsonSerializer.SerializeToElement(salesResult.Value).GetProperty("message").GetString());

        var exportResult = Assert.IsType<BadRequestObjectResult>(await controller.ExportSales(
            DateOnly.MaxValue, DateOnly.MaxValue, cancellationToken: CancellationToken.None));
        Assert.Equal("A data final informada não é válida para este relatório.",
            JsonSerializer.SerializeToElement(exportResult.Value).GetProperty("message").GetString());
    }

    [Fact]
    public async Task SalesReportMarksDisputedSaleAsUnconfirmedRevenue()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var company = new Company { Id = Guid.NewGuid(), Name = "Empresa", Cnpj = "11111111000111" };
        var machine = new Machine
        {
            Id = "disputed-report-machine", Name = "Máquina", CompanyId = company.Id,
            SerialNumber = "DISPUTED-REPORT"
        };
        var transaction = new PaymentTransaction
        {
            Id = Guid.NewGuid(), CompanyId = company.Id, MachineId = machine.Id,
            Amount = 20m, Status = "ReconciliationRequired"
        };
        var session = new MachineSession
        {
            Id = Guid.NewGuid(), CompanyId = company.Id, MachineId = machine.Id,
            TransactionId = transaction.Id, State = MachineSessionStates.Completed
        };
        db.Companies.Add(company);
        db.Machines.Add(machine);
        db.PaymentTransactions.Add(transaction);
        db.MachineSessions.Add(session);
        db.SaleRecords.Add(new SaleRecord
        {
            CompanyId = company.Id, MachineId = machine.Id, SessionId = session.Id,
            TransactionId = transaction.Id, SoldAt = DateTime.UtcNow,
            ProductNameSnapshot = "Água", Quantity = 1, GrossAmount = 20m,
            FinancialStatus = "Disputed"
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, company.Id);
        var day = DateOnly.FromDateTime(DateTime.UtcNow);
        var result = Assert.IsType<OkObjectResult>(await controller.GetSales(day, day, cancellationToken: CancellationToken.None));
        var payload = JsonSerializer.SerializeToElement(result.Value);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("items")[0].GetProperty("NetRevenue").ValueKind);

        var csvStream = new MemoryStream();
        controller.ControllerContext.HttpContext.Response.Body = csvStream;
        Assert.IsType<EmptyResult>(await controller.ExportSales(day, day, cancellationToken: CancellationToken.None));
        var csv = Encoding.UTF8.GetString(csvStream.ToArray());
        Assert.Contains("Em contestação", csv);
        Assert.Contains("Em contestação\"", csv);
    }

    [Fact]
    public async Task SalesCsvStreamsAllRowsWhenTheResultExceedsTheTableLimit()
    {
        const int salesCount = 10_001;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var company = new Company { Id = Guid.NewGuid(), Name = "Empresa", Cnpj = "11111111000111" };
        var machine = new Machine { Id = "full-sales-export", Name = "Máquina", SerialNumber = "FULL-EXPORT", CompanyId = company.Id };
        var soldAt = DateTime.UtcNow.Date.AddDays(-1).AddHours(10);
        var transactions = Enumerable.Range(0, salesCount).Select(_ => new PaymentTransaction
        {
            MachineId = machine.Id,
            CompanyId = company.Id,
            Amount = 1m,
            Status = "Approved"
        }).ToArray();
        var sessions = transactions.Select(transaction => new MachineSession
        {
            Id = Guid.NewGuid(),
            CompanyId = company.Id,
            MachineId = machine.Id,
            TransactionId = transaction.Id,
            State = MachineSessionStates.Completed,
            StartedAt = soldAt,
            LastEventAt = soldAt,
            ClosedAt = soldAt
        }).ToArray();
        var sales = transactions.Zip(sessions, (transaction, session) => new SaleRecord
        {
            CompanyId = company.Id,
            MachineId = machine.Id,
            SessionId = session.Id,
            TransactionId = transaction.Id,
            SoldAt = soldAt,
            ProductNameSnapshot = "Produto",
            Quantity = 1,
            GrossAmount = 1m,
            FinancialStatus = "Settled"
        }).ToArray();

        db.Companies.Add(company);
        db.Machines.Add(machine);
        db.PaymentTransactions.AddRange(transactions);
        db.MachineSessions.AddRange(sessions);
        db.SaleRecords.AddRange(sales);
        await db.SaveChangesAsync();

        var controller = CreateController(db, company.Id);
        var csvStream = new MemoryStream();
        controller.ControllerContext.HttpContext.Response.Body = csvStream;
        Assert.IsType<EmptyResult>(await controller.ExportSales(
            DateOnly.FromDateTime(soldAt), DateOnly.FromDateTime(soldAt), cancellationToken: CancellationToken.None));

        var csv = Encoding.UTF8.GetString(csvStream.ToArray());
        Assert.Equal(salesCount + 1, csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("Produto", csv);
    }

    private static ReportsController CreateController(
        AppDbContext db,
        Guid companyId,
        string role = AppRoles.Admin,
        Guid? impersonatedCompanyId = null)
    {
        var claims = new List<Claim>
        {
            new("company_id", companyId.ToString()),
            new(ClaimTypes.Role, role)
        };
        if (impersonatedCompanyId.HasValue)
            claims.Add(new Claim("impersonated_company_id", impersonatedCompanyId.Value.ToString()));
        return new ReportsController(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))
                }
            }
        };
    }
}
