namespace VendingMachines.Api.Models;

public class Machine
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ClientName { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string Status { get; set; } = "online"; // "online" | "offline" | "warning"
    public int StockLevel { get; set; } // 0-100
    public decimal Revenue30d { get; set; }
    public int TotalSales30d { get; set; }
    public string LastSync { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    public Guid? CompanyId { get; set; }
    public Company? Company { get; set; }
    public Guid? LocationId { get; set; }
    public Location? AssignedLocation { get; set; }
    public bool MercadoPagoEnabled { get; set; } = false;
}
