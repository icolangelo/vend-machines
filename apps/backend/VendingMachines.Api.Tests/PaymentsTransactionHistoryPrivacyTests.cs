using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VendingMachines.Api.Controllers;
using VendingMachines.Api.Data;
using VendingMachines.Api.Models;
using Xunit;

namespace VendingMachines.Api.Tests;

public sealed class PaymentsTransactionHistoryPrivacyTests
{
    [Fact]
    public async Task TransactionHistoryReturnsTenantScopedSummaryWithoutPixQrPayload()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var companyId = Guid.NewGuid();
        var otherCompanyId = Guid.NewGuid();
        db.Companies.AddRange(
            new Company { Id = companyId, Name = "Empresa A", Cnpj = "00000000000100", NormalizedCnpj = "00000000000100" },
            new Company { Id = otherCompanyId, Name = "Empresa B", Cnpj = "00000000000200", NormalizedCnpj = "00000000000200" });
        var machine = new Machine { Id = "machine-company-a", Name = "Máquina A", SerialNumber = "SERIAL-A", CompanyId = companyId };
        var otherMachine = new Machine { Id = "machine-company-b", Name = "Máquina B", SerialNumber = "SERIAL-B", CompanyId = otherCompanyId };
        db.Machines.AddRange(machine, otherMachine);
        db.PaymentTransactions.Add(new PaymentTransaction
        {
            CompanyId = companyId,
            MachineId = machine.Id,
            Machine = machine,
            Amount = 12.50m,
            ApplicationFee = 0.63m,
            Status = "Pending",
            MercadoPagoPaymentId = "payment-company-a",
            QrCode = "PIX_COPY_PASTE_SECRET",
            QrCodeBase64 = "BASE64_QR_IMAGE_SECRET"
        });
        db.PaymentTransactions.Add(new PaymentTransaction
        {
            CompanyId = companyId,
            MachineId = otherMachine.Id,
            Amount = 23m,
            Status = "Pending",
            MercadoPagoPaymentId = "payment-with-foreign-machine",
            QrCode = ""
        });
        db.PaymentTransactions.Add(new PaymentTransaction
        {
            CompanyId = otherCompanyId,
            MachineId = otherMachine.Id,
            Amount = 99m,
            Status = "Approved",
            MercadoPagoPaymentId = "payment-company-b",
            QrCode = "OTHER_TENANT_PIX_SECRET",
            QrCodeBase64 = "OTHER_TENANT_BASE64_SECRET"
        });
        await db.SaveChangesAsync();

        var controller = new PaymentsController(db, null!, null!, null!, null!);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("company_id", companyId.ToString())
                }, "Test"))
            }
        };

        var response = await controller.GetTransactions();
        var json = JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(response).Value);

        Assert.Contains("payment-company-a", json);
        Assert.Contains("machine-company-a", json);
        Assert.Contains("payment-with-foreign-machine", json);
        Assert.DoesNotContain("Máquina B", json);
        Assert.DoesNotContain("SERIAL-B", json);
        Assert.DoesNotContain("payment-company-b", json);
        Assert.DoesNotContain("PIX_COPY_PASTE_SECRET", json);
        Assert.DoesNotContain("BASE64_QR_IMAGE_SECRET", json);
        Assert.DoesNotContain("OTHER_TENANT", json);
        Assert.DoesNotContain("qrCode", json, StringComparison.OrdinalIgnoreCase);
    }
}
