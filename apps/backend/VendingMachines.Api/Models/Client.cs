namespace VendingMachines.Api.Models;

public class Client
{
    public string Name { get; set; } = string.Empty;
    public int MachineCount { get; set; }
    public decimal Revenue30d { get; set; }
    public int TotalSales30d { get; set; }
}
