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

    [Fact]
    public async Task PaginatedTransactionHistoryKeepsTenantScopeAndAppliesMachineStatusAndIdFilters()
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
        db.Machines.AddRange(
            new Machine { Id = "machine-a1", Name = "Máquina A1", SerialNumber = "SERIAL-A1", CompanyId = companyId },
            new Machine { Id = "machine-a2", Name = "Máquina A2", SerialNumber = "SERIAL-A2", CompanyId = companyId },
            new Machine { Id = "machine-b1", Name = "Máquina B1", SerialNumber = "SERIAL-B1", CompanyId = otherCompanyId });

        var baseTime = DateTime.UtcNow;
        var olderPending = new PaymentTransaction
        {
            Id = Guid.NewGuid(), CompanyId = companyId, MachineId = "machine-a1", Amount = 10m,
            Status = "Pending", CreatedAt = baseTime.AddMinutes(-2), MercadoPagoPaymentId = "payment-a-old"
        };
        var newerChargeback = new PaymentTransaction
        {
            Id = Guid.NewGuid(), CompanyId = companyId, MachineId = "machine-a1", Amount = 20m,
            Status = "Chargeback", CreatedAt = baseTime.AddMinutes(-1), MercadoPagoPaymentId = "payment-a-chargeback"
        };
        var newestApproved = new PaymentTransaction
        {
            Id = Guid.NewGuid(), CompanyId = companyId, MachineId = "machine-a2", Amount = 30m,
            Status = "Approved", CreatedAt = baseTime, MercadoPagoPaymentId = "payment-a-newest"
        };
        var foreignApproved = new PaymentTransaction
        {
            Id = Guid.NewGuid(), CompanyId = otherCompanyId, MachineId = "machine-b1", Amount = 99m,
            Status = "Approved", CreatedAt = baseTime.AddMinutes(1), MercadoPagoPaymentId = "payment-b-private"
        };
        db.PaymentTransactions.AddRange(olderPending, newerChargeback, newestApproved, foreignApproved);
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

        var firstPage = Assert.IsType<OkObjectResult>(await controller.GetTransactionHistory(page: 1, pageSize: 2));
        using var firstPageJson = JsonDocument.Parse(JsonSerializer.Serialize(firstPage.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var firstPageRoot = firstPageJson.RootElement;
        Assert.Equal(3, firstPageRoot.GetProperty("total").GetInt32());
        Assert.Equal(1, firstPageRoot.GetProperty("page").GetInt32());
        Assert.Equal(2, firstPageRoot.GetProperty("totalPages").GetInt32());
        var firstItems = firstPageRoot.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, firstItems.Length);
        Assert.Equal(newestApproved.Id.ToString(), firstItems[0].GetProperty("id").GetString());
        Assert.Equal(newerChargeback.Id.ToString(), firstItems[1].GetProperty("id").GetString());
        Assert.DoesNotContain("payment-b-private", firstPageJson.RootElement.GetRawText());

        var secondPage = Assert.IsType<OkObjectResult>(await controller.GetTransactionHistory(page: 2, pageSize: 2));
        using var secondPageJson = JsonDocument.Parse(JsonSerializer.Serialize(secondPage.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(2, secondPageJson.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(olderPending.Id.ToString(), secondPageJson.RootElement.GetProperty("items")[0].GetProperty("id").GetString());

        var filteredPage = Assert.IsType<OkObjectResult>(await controller.GetTransactionHistory(
            page: 1,
            pageSize: 25,
            machineId: "machine-a1",
            transactionId: null,
            status: "Chargeback"));
        using var filteredJson = JsonDocument.Parse(JsonSerializer.Serialize(filteredPage.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(1, filteredJson.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(newerChargeback.Id.ToString(), filteredJson.RootElement.GetProperty("items")[0].GetProperty("id").GetString());

        var crossTenantLookup = Assert.IsType<OkObjectResult>(await controller.GetTransactionHistory(
            transactionId: foreignApproved.Id.ToString()));
        using var crossTenantJson = JsonDocument.Parse(JsonSerializer.Serialize(crossTenantLookup.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(0, crossTenantJson.RootElement.GetProperty("total").GetInt32());
        Assert.Empty(crossTenantJson.RootElement.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task TransactionLogsRedactProviderAndOAuthDetailsButKeepSafeOperationalMessages()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var companyId = Guid.NewGuid();
        var company = new Company { Id = companyId, Name = "Empresa A", Cnpj = "00000000000100", NormalizedCnpj = "00000000000100" };
        var machine = new Machine { Id = "machine-a", Name = "Máquina A", SerialNumber = "SERIAL-A", CompanyId = companyId };
        var transaction = new PaymentTransaction { CompanyId = companyId, MachineId = machine.Id, Amount = 12m, Status = "Approved" };
        db.Companies.Add(company);
        db.Machines.Add(machine);
        db.PaymentTransactions.Add(transaction);
        db.TransactionTelemetryLogs.AddRange(
            new TransactionTelemetryLog
            {
                TransactionId = transaction.Id,
                LogType = "Info",
                Message = "QR Code Pix gerado com sucesso pelo Mercado Pago. Payment ID: provider-payment-private"
            },
            new TransactionTelemetryLog
            {
                TransactionId = transaction.Id,
                LogType = "Info",
                Message = "Usando token OAuth salvo na integração integration-private da empresa company-private. Fingerprint do token: fingerprint-private."
            },
            new TransactionTelemetryLog
            {
                TransactionId = transaction.Id,
                LogType = "Info",
                Message = "Token OAuth validado no Mercado Pago. Seller/User ID: seller-private. Nickname: seller-name-private. Site: MLB."
            },
            new TransactionTelemetryLog
            {
                TransactionId = transaction.Id,
                LogType = "Error",
                Message = "Token OAuth divergente. Integração esperava seller seller-expected-private, mas /users/me retornou seller-current-private."
            },
            new TransactionTelemetryLog
            {
                TransactionId = transaction.Id,
                LogType = "Error",
                Message = "Gateway diagnostic: access_token=unrecognized-private-token contact=private@example.test"
            },
            new TransactionTelemetryLog
            {
                TransactionId = transaction.Id,
                LogType = "Info",
                Message = "Solicitada cobrança Pix de R$ 12,00 na máquina Máquina A."
            });
        await db.SaveChangesAsync();

        var controller = new PaymentsController(db, null!, null!, null!, null!);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("company_id", companyId.ToString()),
                    new Claim(ClaimTypes.Role, "Operator")
                }, "Test"))
            }
        };

        var response = await controller.GetTransactionLogs(transaction.Id);
        var logs = Assert.IsAssignableFrom<IEnumerable<TransactionTelemetryLog>>(Assert.IsType<OkObjectResult>(response).Value);
        var json = JsonSerializer.Serialize(logs);

        Assert.DoesNotContain("provider-payment-private", json);
        Assert.DoesNotContain("integration-private", json);
        Assert.DoesNotContain("company-private", json);
        Assert.DoesNotContain("fingerprint-private", json);
        Assert.DoesNotContain("seller-private", json);
        Assert.DoesNotContain("seller-name-private", json);
        Assert.DoesNotContain("seller-expected-private", json);
        Assert.DoesNotContain("seller-current-private", json);
        Assert.DoesNotContain("unrecognized-private-token", json);
        Assert.DoesNotContain("private@example.test", json);
        Assert.Equal(4, logs.Count(log => log.Message == "Detalhes internos da integração de pagamento foram omitidos."));
        Assert.Contains(logs, log => log.Message.Contains("[REDACTED]", StringComparison.Ordinal));
        Assert.Contains(logs, log => log.Message.StartsWith("Solicitada cobrança Pix", StringComparison.Ordinal));
    }
}
