using System;
using System.Collections.Generic;

namespace VendingMachines.Api.Models;

public class PaymentTransaction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string MachineId { get; set; } = string.Empty;
    public Machine? Machine { get; set; }
    public Guid CompanyId { get; set; }
    public Company? Company { get; set; }
    
    public decimal Amount { get; set; }
    public decimal ApplicationFee { get; set; }
    public string Status { get; set; } = "Pending"; // Pending, Approved, Rejected, Failed, Refunded
    public bool SendTelemetryToMachine { get; set; } = true;
    
    // Mercado Pago Info
    public string? MercadoPagoPaymentId { get; set; }
    public string? MercadoPagoStatus { get; set; }
    public string? MercadoPagoStatusDetail { get; set; }
    public string? RawResponse { get; set; } // Raw JSON string
    
    // Pix details
    public string QrCode { get; set; } = string.Empty;
    public string QrCodeBase64 { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    public ICollection<TransactionTelemetryLog> TelemetryLogs { get; set; } = new List<TransactionTelemetryLog>();
}
