namespace VendingMachines.Api.Models;

public class DashboardStats
{
    public decimal TotalRevenue30d { get; set; }
    public int TotalSales30d { get; set; }
    public int OnlineMachines { get; set; }
    public int WarningMachines { get; set; }
    public int OfflineMachines { get; set; }
    public List<RevenueByDay> RevenueHistory { get; set; } = new();
    public List<HistoricalData> PerformanceHistory { get; set; } = new();
}

public class RevenueByDay
{
    public string Day { get; set; } = string.Empty;
    public decimal Revenue { get; set; }
}

public class HistoricalData
{
    public string Date { get; set; } = string.Empty;
    public decimal Revenue { get; set; }
    public int Products { get; set; }
}
