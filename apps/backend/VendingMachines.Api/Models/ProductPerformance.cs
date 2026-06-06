namespace VendingMachines.Api.Models;

public class ProductPerformance
{
    public string Name { get; set; } = string.Empty;
    public int TotalSold { get; set; }
    public decimal Revenue { get; set; }
    public double Trend { get; set; } // % de mudança
    public bool IsTop { get; set; } // true = top product, false = bottom product
}
