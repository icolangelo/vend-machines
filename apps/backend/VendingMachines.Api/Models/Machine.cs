using System.Text.Json.Serialization;
using System.ComponentModel.DataAnnotations;

namespace VendingMachines.Api.Models;

public class Machine
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ClientName { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string Status { get; set; } = "online"; // "online" | "offline" | "warning"
    public bool IsActive { get; set; } = true;
    public int StockLevel { get; set; } // 0-100
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public bool InventoryConfigured { get; set; }
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public bool LowStock { get; set; }
    public decimal Revenue30d { get; set; }
    public int TotalSales30d { get; set; }
    [Range(1, 10000000)]
    public int? MinimumSalesTarget30d { get; set; }
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public bool BelowSalesTarget => MinimumSalesTarget30d.HasValue && TotalSales30d < MinimumSalesTarget30d.Value;
    public string LastSync { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    public string? NormalizedSerialNumber { get; set; }
    public Guid? CompanyId { get; set; }
    public Company? Company { get; set; }
    public Guid? LocationId { get; set; }
    public Location? AssignedLocation { get; set; }
    public bool MercadoPagoEnabled { get; set; } = false;
    public bool AutoOpenSessionEnabled { get; set; } = false;

    // A credencial nunca é retornada pela API; somente o hash fica persistido.
    [JsonIgnore]
    public string? TelemetryCredentialHash { get; set; }

    [JsonIgnore]
    public DateTime? TelemetryCredentialCreatedAt { get; set; }

    [JsonIgnore]
    public DateTime? TelemetryCredentialRevokedAt { get; set; }
}
