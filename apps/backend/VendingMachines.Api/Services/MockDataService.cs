using VendingMachines.Api.Models;

namespace VendingMachines.Api.Services;

public class MockDataService : IMockDataService
{
    private readonly List<ProductType> _productTypes = new()
    {
        new ProductType { Id = "pt-1", Name = "Bebidas Frias" },
        new ProductType { Id = "pt-2", Name = "Bebidas Quentes" },
        new ProductType { Id = "pt-3", Name = "Carne" },
        new ProductType { Id = "pt-4", Name = "Doces" },
        new ProductType { Id = "pt-5", Name = "Outros" },
        new ProductType { Id = "pt-6", Name = "Refeição" },
        new ProductType { Id = "pt-7", Name = "Sanduíche" },
        new ProductType { Id = "pt-8", Name = "Snacks" },
        new ProductType { Id = "pt-9", Name = "Yogurt" },
    };

    private readonly List<Machine> _machines = new()
    {
        new Machine { Id = "VM-001", Name = "Lobby A1", ClientName = "Hospital São Luiz", Location = "Lobby Principal", Status = "online", StockLevel = 82, Revenue30d = 4230, TotalSales30d = 847, LastSync = "2 min atrás" },
        new Machine { Id = "VM-002", Name = "Refeitório B2", ClientName = "Hospital São Luiz", Location = "Refeitório 2º andar", Status = "warning", StockLevel = 18, Revenue30d = 3890, TotalSales30d = 778, LastSync = "5 min atrás" },
        new Machine { Id = "VM-003", Name = "UTI C1", ClientName = "Hospital São Luiz", Location = "Corredor UTI", Status = "online", StockLevel = 65, Revenue30d = 2150, TotalSales30d = 430, LastSync = "1 min atrás" },
        new Machine { Id = "VM-004", Name = "Recepção D1", ClientName = "Faculdade Anhanguera", Location = "Bloco D", Status = "online", StockLevel = 91, Revenue30d = 5670, TotalSales30d = 1134, LastSync = "3 min atrás" },
        new Machine { Id = "VM-005", Name = "Cantina E1", ClientName = "Faculdade Anhanguera", Location = "Cantina Central", Status = "offline", StockLevel = 0, Revenue30d = 0, TotalSales30d = 0, LastSync = "2 dias atrás" },
        new Machine { Id = "VM-006", Name = "Hall F1", ClientName = "Condomínio Alphaville", Location = "Hall de Entrada", Status = "online", StockLevel = 45, Revenue30d = 1890, TotalSales30d = 378, LastSync = "4 min atrás" },
        new Machine { Id = "VM-007", Name = "Academia G1", ClientName = "Condomínio Alphaville", Location = "Academia", Status = "warning", StockLevel = 12, Revenue30d = 3420, TotalSales30d = 684, LastSync = "8 min atrás" },
        new Machine { Id = "VM-008", Name = "Terminal H1", ClientName = "Rodoviária Tietê", Location = "Terminal 3", Status = "online", StockLevel = 73, Revenue30d = 8940, TotalSales30d = 1788, LastSync = "1 min atrás" },
        new Machine { Id = "VM-009", Name = "Embarque I1", ClientName = "Rodoviária Tietê", Location = "Sala de Embarque", Status = "online", StockLevel = 56, Revenue30d = 7230, TotalSales30d = 1446, LastSync = "2 min atrás" },
        new Machine { Id = "VM-010", Name = "Plataforma J1", ClientName = "Rodoviária Tietê", Location = "Plataforma 12", Status = "warning", StockLevel = 22, Revenue30d = 6180, TotalSales30d = 1236, LastSync = "15 min atrás" },
        new Machine { Id = "VM-011", Name = "Escritório K1", ClientName = "WeWork Faria Lima", Location = "12º andar", Status = "online", StockLevel = 88, Revenue30d = 4560, TotalSales30d = 912, LastSync = "1 min atrás" },
        new Machine { Id = "VM-012", Name = "Lounge L1", ClientName = "WeWork Faria Lima", Location = "Lounge Café", Status = "online", StockLevel = 71, Revenue30d = 5230, TotalSales30d = 1046, LastSync = "3 min atrás" },
    };

    private readonly List<Client> _clients = new()
    {
        new Client { Name = "Rodoviária Tietê", MachineCount = 3, Revenue30d = 22350, TotalSales30d = 4470 },
        new Client { Name = "Faculdade Anhanguera", MachineCount = 2, Revenue30d = 5670, TotalSales30d = 1134 },
        new Client { Name = "Hospital São Luiz", MachineCount = 3, Revenue30d = 10270, TotalSales30d = 2055 },
        new Client { Name = "WeWork Faria Lima", MachineCount = 2, Revenue30d = 9790, TotalSales30d = 1958 },
        new Client { Name = "Condomínio Alphaville", MachineCount = 2, Revenue30d = 5310, TotalSales30d = 1062 },
    };

    private readonly List<Product> _topProducts = new()
    {
        new Product { Name = "Água Mineral 500ml", TotalSold = 3240, Revenue = 9720, Trend = 12.3 },
        new Product { Name = "Coca-Cola Lata", TotalSold = 2890, Revenue = 14450, Trend = 8.1 },
        new Product { Name = "Barra de Cereal", TotalSold = 1670, Revenue = 5845, Trend = 5.4 },
        new Product { Name = "Suco Del Valle", TotalSold = 1420, Revenue = 7100, Trend = -2.1 },
        new Product { Name = "Biscoito Oreo", TotalSold = 1180, Revenue = 4720, Trend = 15.7 },
    };

    private readonly List<Product> _bottomProducts = new()
    {
        new Product { Name = "Chá Gelado Leão", TotalSold = 89, Revenue = 356, Trend = -28.4 },
        new Product { Name = "Amendoim Japonês", TotalSold = 112, Revenue = 336, Trend = -19.2 },
        new Product { Name = "Energético Monster", TotalSold = 156, Revenue = 1248, Trend = -12.8 },
        new Product { Name = "Paçoca Amor", TotalSold = 178, Revenue = 356, Trend = -8.5 },
        new Product { Name = "Vitamina Yakult", TotalSold = 203, Revenue = 609, Trend = -5.1 },
    };

    private readonly List<FullProduct> _products = new()
    {
        new FullProduct { Id = "p-1", Code = "BEB-001", Name = "Coca-Cola Lata", Description = "Refrigerante de cola 350ml", TypeId = "pt-1", IsAlcoholic = false, Cost = 2.500m },
        new FullProduct { Id = "p-2", Code = "ALC-001", Name = "Cerveja Heineken", Description = "Cerveja lager premium 330ml", TypeId = "pt-1", IsAlcoholic = true, Cost = 4.850m },
        new FullProduct { Id = "p-3", Code = "SNA-001", Name = "Batata Pringles", Description = "Batata frita sabor original 114g", TypeId = "pt-8", IsAlcoholic = false, Cost = 12.300m },
    };

    public List<Machine> GetMachines() => _machines;
    public List<Client> GetClients() => _clients;
    public List<Product> GetTopProducts() => _topProducts;
    public List<Product> GetBottomProducts() => _bottomProducts;
    public List<FullProduct> GetProducts() => _products;
    public List<ProductType> GetProductTypes() => _productTypes;

    public DashboardStats GetDashboardStats()
    {
        return new DashboardStats
        {
            TotalRevenue30d = _machines.Sum(m => m.Revenue30d),
            TotalSales30d = _machines.Sum(m => m.TotalSales30d),
            OnlineMachines = _machines.Count(m => m.Status == "online"),
            WarningMachines = _machines.Count(m => m.Status == "warning"),
            OfflineMachines = _machines.Count(m => m.Status == "offline"),
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
}
