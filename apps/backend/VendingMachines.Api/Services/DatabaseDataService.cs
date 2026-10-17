using Microsoft.EntityFrameworkCore;
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

    public List<Machine> GetMachines(Guid? companyId = null)
    {
        var now = DateTime.UtcNow;
        var machines = GetMachineQuery(companyId).ToList();
        var machineIds = machines.Select(machine => machine.Id).ToList();
        var since = now.AddDays(-30);
        var salesQuery = _context.SaleRecords.AsNoTracking()
            .Where(sale => machineIds.Contains(sale.MachineId) && sale.SoldAt >= since);
        if (companyId.HasValue)
        {
            salesQuery = salesQuery.Where(sale => sale.CompanyId == companyId.Value);
        }
        var sales = salesQuery
            .GroupBy(sale => sale.MachineId)
            .Select(group => new
            {
                MachineId = group.Key,
                Revenue = group.Sum(sale => sale.GrossAmount - sale.RefundedAmount),
                Quantity = group.Sum(sale => sale.Quantity)
            })
            .ToDictionary(group => group.MachineId);

        var connectionStatesQuery = _context.MachineConnectionStates.AsNoTracking()
            .Where(state => machineIds.Contains(state.MachineId));
        if (companyId.HasValue)
        {
            connectionStatesQuery = connectionStatesQuery.Where(state => state.CompanyId == companyId.Value);
        }
        var connectionStates = connectionStatesQuery
            .ToDictionary(state => state.MachineId);
        var stockQuery = _context.MachineProductSlots.AsNoTracking()
            .Where(slot => machineIds.Contains(slot.MachineId));
        if (companyId.HasValue)
        {
            stockQuery = stockQuery.Where(slot => slot.CompanyId == companyId.Value);
        }
        var stockByMachine = stockQuery
            .GroupBy(slot => slot.MachineId)
            .Select(group => new
            {
                MachineId = group.Key,
                Capacity = group.Sum(slot => slot.Capacity),
                Current = group.Sum(slot => slot.CurrentStock),
                LowStock = group.Any(slot => slot.CurrentStock <= slot.LowStockThreshold)
            })
            .ToDictionary(group => group.MachineId);

        foreach (var machine in machines)
        {
            if (sales.TryGetValue(machine.Id, out var totals))
            {
                machine.Revenue30d = totals.Revenue;
                machine.TotalSales30d = totals.Quantity;
            }
            else
            {
                machine.Revenue30d = 0m;
                machine.TotalSales30d = 0;
            }
            if (stockByMachine.TryGetValue(machine.Id, out var stock) && stock.Capacity > 0)
            {
                machine.InventoryConfigured = true;
                machine.LowStock = stock.LowStock;
                machine.StockLevel = Math.Clamp((int)Math.Round(stock.Current * 100d / stock.Capacity), 0, 100);
            }
            else
            {
                machine.InventoryConfigured = false;
                machine.LowStock = false;
                machine.StockLevel = 0;
            }

            if (!machine.IsActive)
            {
                machine.Status = "inactive";
                machine.LastSync = "sem monitoramento";
                continue;
            }

            if (!connectionStates.TryGetValue(machine.Id, out var connection) || !connection.MonitoringEnabled)
            {
                machine.Status = "unmonitored";
                machine.LastSync = "sem monitoramento";
                continue;
            }

            var lastSeen = connection.LastSeenAt ?? connection.ConnectedAt;
            machine.Status = connection.IsOnline && lastSeen >= now.AddMinutes(-2)
                ? "online"
                : lastSeen >= now.AddMinutes(-5) ? "warning" : "offline";
            machine.LastSync = FormatLastSeen(lastSeen, now);
        }

        return machines;
    }

    private static string FormatLastSeen(DateTime? lastSeen, DateTime now)
    {
        if (!lastSeen.HasValue) return "sem comunicação";
        var elapsed = now - lastSeen.Value;
        if (elapsed < TimeSpan.FromMinutes(1)) return "agora";
        if (elapsed < TimeSpan.FromHours(1)) return $"há {(int)elapsed.TotalMinutes} min";
        if (elapsed < TimeSpan.FromDays(1)) return $"há {(int)elapsed.TotalHours} h";
        return $"há {(int)elapsed.TotalDays} dias";
    }

    public List<LocationDto> GetLocations(Guid? companyId = null)
    {
        var locations = _context.Locations.AsNoTracking().AsQueryable();
        if (companyId.HasValue)
        {
            locations = locations.Where(l => l.CompanyId == companyId.Value);
        }
        var locationList = locations.ToList();
        var machineList = GetMachineQuery(companyId).AsNoTracking().ToList();
        var machineIds = machineList.Select(machine => machine.Id).ToList();
        var since = DateTime.UtcNow.AddDays(-30);
        var salesByMachineQuery = _context.SaleRecords.AsNoTracking()
            .Where(sale => machineIds.Contains(sale.MachineId) && sale.SoldAt >= since);
        if (companyId.HasValue)
        {
            salesByMachineQuery = salesByMachineQuery.Where(sale => sale.CompanyId == companyId.Value);
        }
        var salesByMachine = salesByMachineQuery
            .GroupBy(sale => sale.MachineId)
            .Select(group => new
            {
                MachineId = group.Key,
                Revenue = group.Sum(sale => sale.GrossAmount - sale.RefundedAmount),
                Sales = group.Sum(sale => sale.Quantity)
            })
            .ToDictionary(group => group.MachineId);

        return locationList.Select(location =>
            {
                var locationMachines = machineList.Where(machine => machine.LocationId == location.Id).ToList();
                var machineTotals = locationMachines
                    .Where(machine => salesByMachine.ContainsKey(machine.Id))
                    .Select(machine => salesByMachine[machine.Id])
                    .ToList();
                return new LocationDto
                {
                    Id = location.Id,
                    CompanyId = location.CompanyId,
                    Name = location.Name,
                    MachineCount = locationMachines.Count,
                    Revenue30d = machineTotals.Sum(total => total.Revenue),
                    TotalSales30d = machineTotals.Sum(total => total.Sales),
                    CreatedAt = location.CreatedAt,
                    UpdatedAt = location.UpdatedAt
                };
            })
            .OrderBy(location => location.Name)
            .ToList();
    }

    public List<Location> CreateDefaultLocations(Company company)
    {
        var prefix = GetCompanyPrefix(company.Name);
        return new List<Location>
        {
            new() { CompanyId = company.Id, Name = $"{prefix} - Shopping", CreatedAt = DateTime.UtcNow },
            new() { CompanyId = company.Id, Name = $"{prefix} - Hospital", CreatedAt = DateTime.UtcNow }
        };
    }

    public static string GetCompanyPrefix(string companyName)
    {
        var trimmed = companyName.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return "Empresa";
        }

        return trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "Empresa";
    }

    public void SyncMachineLocation(Machine machine, Guid companyId)
    {
        if (!machine.LocationId.HasValue)
        {
            throw new InvalidOperationException("Selecione uma localização para a máquina.");
        }

        var location = _context.Locations
            .AsNoTracking()
            .FirstOrDefault(l => l.Id == machine.LocationId.Value);

        if (location == null)
        {
            throw new InvalidOperationException("A localização selecionada não foi encontrada. Atualize a lista de localizações e tente novamente.");
        }

        if (location.CompanyId != companyId)
        {
            throw new InvalidOperationException("A localização selecionada não pertence à empresa logada. Atualize a lista de localizações e tente novamente.");
        }

        machine.ClientName = location.Name;
        if (string.IsNullOrWhiteSpace(machine.Location))
        {
            machine.Location = location.Name;
        }
    }

    public List<Client> GetLegacyClients(Guid? companyId = null)
    {
        return GetLocations(companyId)
            .Select(l => new Client
            {
                Name = l.Name,
                MachineCount = l.MachineCount,
                Revenue30d = l.Revenue30d,
                TotalSales30d = l.TotalSales30d
            })
            .ToList();
    }

    public List<Product> GetTopProducts(Guid? companyId = null)
    {
        return GetCompanyScopedProductPerformance(companyId, top: true);
    }

    public List<Product> GetBottomProducts(Guid? companyId = null)
    {
        return GetCompanyScopedProductPerformance(companyId, top: false);
    }

    public List<FullProduct> GetProducts(Guid? companyId = null)
    {
        var query = _context.Products.AsQueryable();
        if (companyId.HasValue)
        {
            query = query.Where(p => p.CompanyId == null || p.CompanyId == companyId.Value);
        }
        var list = query.ToList();
        if (companyId.HasValue)
        {
            var overriddenIds = list.Where(p => p.OriginalId != null && p.CompanyId == companyId.Value).Select(p => p.OriginalId).ToList();
            list = list.Where(p => !overriddenIds.Contains(p.Id)).ToList();
        }
        return list;
    }

    public List<ProductType> GetProductTypes(Guid? companyId = null)
    {
        var query = _context.ProductTypes.AsQueryable();
        if (companyId.HasValue)
        {
            query = query.Where(pt => pt.CompanyId == null || pt.CompanyId == companyId.Value);
        }
        var list = query.ToList();
        if (companyId.HasValue)
        {
            var overriddenIds = list.Where(pt => pt.OriginalId != null && pt.CompanyId == companyId.Value).Select(pt => pt.OriginalId).ToList();
            Console.WriteLine($"[DEBUG] overriddenIds: {string.Join(", ", overriddenIds)}");
            foreach(var item in list) {
                Console.WriteLine($"[DEBUG] item Id={item.Id} Name={item.Name} OriginalId={item.OriginalId}");
            }
            list = list.Where(pt => !overriddenIds.Contains(pt.Id)).ToList();
        }
        return list;
    }

    public DashboardStats GetDashboardStats(Guid? companyId = null)
    {
        var machines = GetMachines(companyId);
        var since = DateTime.UtcNow.AddDays(-30);
        var sales = GetSalesQuery(companyId, since).ToList();
        var performanceHistory = GenerateSalesHistory(sales, 30);
        return new DashboardStats
        {
            TotalRevenue30d = sales.Sum(sale => sale.GrossAmount - sale.RefundedAmount),
            TotalSales30d = sales.Sum(sale => sale.Quantity),
            OnlineMachines = machines.Count(m => m.Status == "online"),
            WarningMachines = machines.Count(m => m.Status == "warning"),
            OfflineMachines = machines.Count(m => m.Status == "offline"),
            RevenueHistory = performanceHistory
                .TakeLast(7)
                .Select(h => new RevenueByDay { Day = h.Date, Revenue = h.Revenue })
                .ToList(),
            PerformanceHistory = performanceHistory
        };
    }

    private IQueryable<Machine> GetMachineQuery(Guid? companyId = null)
    {
        var query = _context.Machines.AsQueryable();
        if (companyId.HasValue)
        {
            query = query.Where(m => m.CompanyId == companyId.Value);
        }
        return query;
    }

    private IQueryable<SaleRecord> GetSalesQuery(Guid? companyId, DateTime startDate)
    {
        var query = _context.SaleRecords.AsNoTracking().Where(sale => sale.SoldAt >= startDate);

        if (companyId.HasValue)
        {
            query = query.Where(sale => sale.CompanyId == companyId.Value);
        }

        return query;
    }

    private List<Product> GetCompanyScopedProductPerformance(Guid? companyId, bool top)
    {
        if (!companyId.HasValue)
        {
            return new List<Product>();
        }

        var now = DateTime.UtcNow;
        var currentStart = now.AddDays(-30);
        var previousStart = now.AddDays(-60);
        var sales = _context.SaleRecords.AsNoTracking()
            .Where(sale => sale.CompanyId == companyId.Value && sale.ProductId != null && sale.SoldAt >= previousStart)
            .ToList();
        var currentSales = sales.Where(sale => sale.SoldAt >= currentStart)
            .GroupBy(sale => sale.ProductId!)
            .ToDictionary(group => group.Key, group => new { Count = group.Sum(sale => sale.Quantity), Revenue = group.Sum(sale => sale.GrossAmount - sale.RefundedAmount) });
        var previousSales = sales.Where(sale => sale.SoldAt < currentStart)
            .GroupBy(sale => sale.ProductId!)
            .ToDictionary(group => group.Key, group => group.Sum(sale => sale.Quantity));

        var assignedProductIds = _context.MachineProductSlots.AsNoTracking()
            .Where(slot => slot.CompanyId == companyId.Value)
            .Select(slot => slot.ProductId)
            .Distinct()
            .ToList();
        var productIds = top
            ? currentSales.Where(sale => sale.Value.Count > 0).Select(sale => sale.Key).ToList()
            : assignedProductIds;
        if (productIds.Count == 0)
        {
            return new List<Product>();
        }

        var products = _context.Products.AsNoTracking()
            .Where(product => productIds.Contains(product.Id) &&
                              (product.CompanyId == null || product.CompanyId == companyId.Value))
            .ToDictionary(product => product.Id);
        var snapshots = sales.Where(sale => sale.ProductId != null && !string.IsNullOrWhiteSpace(sale.ProductNameSnapshot))
            .GroupBy(sale => sale.ProductId!)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(sale => sale.SoldAt).First().ProductNameSnapshot!);

        var ranked = productIds.Distinct().Select(productId =>
        {
            currentSales.TryGetValue(productId, out var current);
            previousSales.TryGetValue(productId, out var previous);
            products.TryGetValue(productId, out var product);
            snapshots.TryGetValue(productId, out var snapshot);
            var currentCount = current?.Count ?? 0;
            var previousCount = previous;
            var trend = previousCount > 0
                ? (currentCount - previousCount) * 100d / previousCount
                : currentCount > 0 ? 100d : 0d;
            return new Product
            {
                Name = snapshot ?? product?.Name ?? "Produto removido",
                TotalSold = currentCount,
                Revenue = current?.Revenue ?? 0m,
                Trend = trend
            };
        });

        return top
            ? ranked.OrderByDescending(product => product.TotalSold).ThenByDescending(product => product.Revenue).Take(5).ToList()
            : ranked.OrderBy(product => product.TotalSold).ThenBy(product => product.Revenue).Take(5).ToList();
    }

    private List<HistoricalData> GenerateSalesHistory(List<SaleRecord> sales, int days)
    {
        var data = new List<HistoricalData>();
        var today = DateTime.UtcNow.Date;
        var salesByDate = sales
            .GroupBy(sale => sale.SoldAt.Date)
            .ToDictionary(g => g.Key, g => new
            {
                Revenue = g.Sum(sale => sale.GrossAmount - sale.RefundedAmount),
                Products = g.Sum(sale => sale.Quantity)
            });

        for (int i = days - 1; i >= 0; i--)
        {
            var date = today.AddDays(-i);
            salesByDate.TryGetValue(date, out var totals);

            data.Add(new HistoricalData
            {
                Date = date.ToString("dd/MM"),
                Revenue = totals?.Revenue ?? 0,
                Products = totals?.Products ?? 0
            });
        }
        return data;
    }

    public void AddMachine(Machine machine)
    {
        if (!machine.CompanyId.HasValue)
        {
            throw new InvalidOperationException("A máquina precisa estar vinculada a uma empresa.");
        }

        SyncMachineLocation(machine, machine.CompanyId.Value);
        machine.NormalizedSerialNumber = NormalizeSerial(machine.SerialNumber);

        if (machine.NormalizedSerialNumber == null)
        {
            throw new InvalidOperationException("A máquina precisa possuir um número de série.");
        }

        if (_context.Machines.Any(m => m.NormalizedSerialNumber == machine.NormalizedSerialNumber))
        {
            throw new InvalidOperationException("Já existe uma máquina cadastrada com este número de série.");
        }

        if (string.IsNullOrWhiteSpace(machine.Id))
        {
            machine.Id = GenerateMachineId();
        }

        if (_context.Machines.Any(m => m.Id == machine.Id))
        {
            throw new InvalidOperationException("Já existe uma máquina cadastrada com este ID.");
        }

        _context.Machines.Add(machine);
        _context.SaveChanges();
    }

    public FullProduct AddProduct(FullProduct product)
    {
        if (!product.CompanyId.HasValue)
        {
            throw new InvalidOperationException("O produto precisa estar vinculado a uma empresa.");
        }
        if (string.IsNullOrWhiteSpace(product.Id))
        {
            product.Id = $"p-{Guid.NewGuid().ToString("N")[..8]}";
        }
        _context.Products.Add(product);
        _context.SaveChanges();
        return product;
    }

    public FullProduct UpdateProduct(FullProduct product)
    {
        var existing = _context.Products.FirstOrDefault(p => p.Id == product.Id);
        if (existing == null) throw new InvalidOperationException("Produto não encontrado.");

        if (!product.CompanyId.HasValue)
        {
            throw new InvalidOperationException("Para editar, a empresa deve ser informada.");
        }

        if (existing.CompanyId == null)
        {
            // Copy-on-write para produto padrão
            var newProduct = new FullProduct
            {
                Id = $"p-{Guid.NewGuid().ToString("N")[..8]}",
                Code = product.Code,
                Name = product.Name,
                Description = product.Description,
                TypeId = product.TypeId,
                IsAlcoholic = product.IsAlcoholic,
                Cost = product.Cost,
                CompanyId = product.CompanyId,
                OriginalId = existing.Id
            };
            _context.Products.Add(newProduct);
            _context.SaveChanges();
            return newProduct;
        }

        if (existing.CompanyId != product.CompanyId)
        {
            throw new InvalidOperationException("Produto não pertence à empresa atual.");
        }

        existing.Code = product.Code;
        existing.Name = product.Name;
        existing.Description = product.Description;
        existing.TypeId = product.TypeId;
        existing.IsAlcoholic = product.IsAlcoholic;
        existing.Cost = product.Cost;
        
        _context.SaveChanges();
        return existing;
    }

    public ProductType AddProductType(ProductType productType)
    {
        if (!productType.CompanyId.HasValue)
        {
            throw new InvalidOperationException("O tipo de produto precisa estar vinculado a uma empresa.");
        }
        if (string.IsNullOrWhiteSpace(productType.Id))
        {
            productType.Id = $"pt-{Guid.NewGuid().ToString("N")[..8]}";
        }
        _context.ProductTypes.Add(productType);
        _context.SaveChanges();
        return productType;
    }

    public ProductType UpdateProductType(ProductType productType)
    {
        var existing = _context.ProductTypes.FirstOrDefault(pt => pt.Id == productType.Id);
        if (existing == null) throw new InvalidOperationException("Tipo de produto não encontrado.");

        if (!productType.CompanyId.HasValue)
        {
            throw new InvalidOperationException("Para editar, a empresa deve ser informada.");
        }

        if (existing.CompanyId == null)
        {
            // Copy-on-write para tipo padrão
            var newProductType = new ProductType
            {
                Id = $"pt-{Guid.NewGuid().ToString("N")[..8]}",
                Name = productType.Name,
                CompanyId = productType.CompanyId,
                OriginalId = existing.Id
            };
            _context.ProductTypes.Add(newProductType);

            // Cascade copy-on-write para os produtos vinculados
            var linkedProducts = _context.Products.Where(p => p.TypeId == existing.Id && (p.CompanyId == null || p.CompanyId == productType.CompanyId)).ToList();
            foreach (var p in linkedProducts)
            {
                if (p.CompanyId == null)
                {
                    var newProduct = new FullProduct
                    {
                        Id = $"p-{Guid.NewGuid().ToString("N")[..8]}",
                        Code = p.Code,
                        Name = p.Name,
                        Description = p.Description,
                        TypeId = newProductType.Id,
                        IsAlcoholic = p.IsAlcoholic,
                        Cost = p.Cost,
                        CompanyId = productType.CompanyId,
                        OriginalId = p.Id
                    };
                    _context.Products.Add(newProduct);
                }
                else
                {
                    p.TypeId = newProductType.Id;
                }
            }

            _context.SaveChanges();
            return newProductType;
        }

        if (existing.CompanyId != productType.CompanyId)
        {
            throw new InvalidOperationException("Tipo de produto não pertence à empresa atual.");
        }

        existing.Name = productType.Name;
        
        _context.SaveChanges();
        return existing;
    }

    private string GenerateMachineId()
    {
        string id;
        do
        {
            id = $"VM-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";
        } while (_context.Machines.Any(m => m.Id == id));

        return id;
    }

    public void UpdateMachine(Machine machine)
    {
        var existing = _context.Machines.FirstOrDefault(m => m.Id == machine.Id);
        if (existing != null)
        {
            if (!machine.CompanyId.HasValue)
            {
                throw new InvalidOperationException("A máquina precisa estar vinculada a uma empresa.");
            }

            SyncMachineLocation(machine, machine.CompanyId.Value);

            existing.Name = machine.Name;
            existing.ClientName = machine.ClientName;
            existing.Location = machine.Location;
            existing.LastSync = machine.LastSync;
            existing.SerialNumber = machine.SerialNumber;
            existing.NormalizedSerialNumber = NormalizeSerial(machine.SerialNumber);
            if (existing.NormalizedSerialNumber == null)
            {
                throw new InvalidOperationException("A máquina precisa possuir um número de série.");
            }
            if (_context.Machines.Any(m => m.Id != existing.Id && m.NormalizedSerialNumber == existing.NormalizedSerialNumber))
            {
                throw new InvalidOperationException("Já existe uma máquina cadastrada com este número de série.");
            }
            existing.CompanyId = machine.CompanyId;
            existing.LocationId = machine.LocationId;
            existing.MercadoPagoEnabled = machine.MercadoPagoEnabled;
            existing.AutoOpenSessionEnabled = machine.AutoOpenSessionEnabled;
            existing.IsActive = machine.IsActive;
            existing.MinimumSalesTarget30d = machine.MinimumSalesTarget30d;
            _context.SaveChanges();
        }
    }

    private static string? NormalizeSerial(string? serial)
    {
        var normalized = serial?.Trim().ToUpperInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
