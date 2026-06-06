using VendingMachines.Api.Data;
using VendingMachines.Api.Models;

namespace VendingMachines.Api.Services;

public class DatabaseDataService : IDataService
{
    private readonly AppDbContext _context;

    public DatabaseDataService(AppDbContext context)
    {
        _context = context;
    }

    public List<Machine> GetMachines() => _context.Machines.ToList();

    public List<Client> GetClients()
    {
        return _context.Machines
            .GroupBy(m => m.ClientName)
            .Select(g => new Client
            {
                Name = g.Key,
                MachineCount = g.Count(),
                Revenue30d = g.Sum(m => m.Revenue30d),
                TotalSales30d = g.Sum(m => m.TotalSales30d)
            })
            .ToList();
    }

    public List<Product> GetTopProducts()
    {
        return _context.ProductPerformances
            .Where(p => p.IsTop)
            .Select(p => new Product
            {
                Name = p.Name,
                TotalSold = p.TotalSold,
                Revenue = p.Revenue,
                Trend = p.Trend
            })
            .ToList();
    }

    public List<Product> GetBottomProducts()
    {
        return _context.ProductPerformances
            .Where(p => !p.IsTop)
            .Select(p => new Product
            {
                Name = p.Name,
                TotalSold = p.TotalSold,
                Revenue = p.Revenue,
                Trend = p.Trend
            })
            .ToList();
    }

    public List<FullProduct> GetProducts() => _context.Products.ToList();

    public List<ProductType> GetProductTypes() => _context.ProductTypes.ToList();

    public DashboardStats GetDashboardStats()
    {
        var machines = _context.Machines.ToList();
        return new DashboardStats
        {
            TotalRevenue30d = machines.Sum(m => m.Revenue30d),
            TotalSales30d = machines.Sum(m => m.TotalSales30d),
            OnlineMachines = machines.Count(m => m.Status == "online"),
            WarningMachines = machines.Count(m => m.Status == "warning"),
            OfflineMachines = machines.Count(m => m.Status == "offline"),
            RevenueHistory = new List<RevenueByDay>
            {
                new() { Day = "Seg", Revenue = 2840 },
                new() { Day = "Ter", Revenue = 3120 },
                new() { Day = "Qua", Revenue = 2960 },
                new() { Day = "Qui", Revenue = 3450 },
                new() { Day = "Sex", Revenue = 3890 },
                new() { Day = "Sáb", Revenue = 4210 },
                new() { Day = "Dom", Revenue = 2180 }
            },
            PerformanceHistory = GenerateHistoricalData(30)
        };
    }

    private List<HistoricalData> GenerateHistoricalData(int days)
    {
        var data = new List<HistoricalData>();
        var today = DateTime.Today;
        var random = new Random();

        for (int i = days - 1; i >= 0; i--)
        {
            var date = today.AddDays(-i);
            decimal baseRevenue = 2000 + (decimal)(random.NextDouble() * 3000);
            
            if (date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday)
            {
                baseRevenue *= 0.8m;
            }

            int baseProducts = (int)(baseRevenue / 10) + random.Next(50);

            data.Add(new HistoricalData
            {
                Date = date.ToString("dd/MM"),
                Revenue = Math.Round(baseRevenue),
                Products = baseProducts
            });
        }
        return data;
    }

    public void AddMachine(Machine machine)
    {
        _context.Machines.Add(machine);
        _context.SaveChanges();
    }

    public void UpdateMachine(Machine machine)
    {
        var existing = _context.Machines.FirstOrDefault(m => m.Id == machine.Id);
        if (existing != null)
        {
            existing.Name = machine.Name;
            existing.ClientName = machine.ClientName;
            existing.Location = machine.Location;
            existing.Status = machine.Status;
            existing.StockLevel = machine.StockLevel;
            existing.Revenue30d = machine.Revenue30d;
            existing.TotalSales30d = machine.TotalSales30d;
            existing.LastSync = machine.LastSync;
            existing.SerialNumber = machine.SerialNumber;
            _context.SaveChanges();
        }
    }
}
