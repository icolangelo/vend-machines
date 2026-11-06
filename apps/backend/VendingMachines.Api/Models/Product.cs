namespace VendingMachines.Api.Models;

public class Product
{
    public string Name { get; set; } = string.Empty;
    public int TotalSold { get; set; }
    public decimal Revenue { get; set; }
    public double Trend { get; set; } // % change
}
