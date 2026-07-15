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
        return GetMachineQuery(companyId).ToList();
    }

    public List<LocationDto> GetLocations(Guid? companyId = null)
    {
        var locations = _context.Locations.AsQueryable();
        if (companyId.HasValue)
        {
            locations = locations.Where(l => l.CompanyId == companyId.Value);
        }

        return locations
            .GroupJoin(
                GetMachineQuery(companyId),
                location => location.Id,
                machine => machine.LocationId,
                (location, machines) => new LocationDto
                {
                    Id = location.Id,
                    CompanyId = location.CompanyId,
                    Name = location.Name,
                    MachineCount = machines.Count(),
                    Revenue30d = machines.Sum(m => (decimal?)m.Revenue30d) ?? 0m,
                    TotalSales30d = machines.Sum(m => (int?)m.TotalSales30d) ?? 0,
                    CreatedAt = location.CreatedAt,
                    UpdatedAt = location.UpdatedAt
                })
            .OrderBy(l => l.Name)
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
        return GetCompanyScopedProductPerformance(companyId);
    }

    public List<Product> GetBottomProducts(Guid? companyId = null)
    {
        return GetCompanyScopedProductPerformance(companyId);
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
        var machines = GetMachineQuery(companyId).ToList();
        var approvedTransactions = GetApprovedTransactionQuery(companyId, DateTime.UtcNow.AddDays(-30)).ToList();
        var performanceHistory = GenerateTransactionHistory(approvedTransactions, 30);
        return new DashboardStats
        {
            TotalRevenue30d = approvedTransactions.Sum(t => t.Amount),
            TotalSales30d = approvedTransactions.Count,
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

    private IQueryable<PaymentTransaction> GetApprovedTransactionQuery(Guid? companyId, DateTime startDate)
    {
        var query = _context.PaymentTransactions
            .Where(t => t.Status == "Approved" && (t.CompletedAt ?? t.CreatedAt) >= startDate);

        if (companyId.HasValue)
        {
            query = query.Where(t => t.CompanyId == companyId.Value);
        }

        return query;
    }

    private List<Product> GetCompanyScopedProductPerformance(Guid? companyId)
    {
        if (!companyId.HasValue || !GetMachineQuery(companyId).Any())
        {
            return new List<Product>();
        }

        var hasApprovedSales = GetApprovedTransactionQuery(companyId, DateTime.UtcNow.AddDays(-30)).Any();
        if (!hasApprovedSales)
        {
            return new List<Product>();
        }

        return new List<Product>();
    }

    private List<HistoricalData> GenerateTransactionHistory(List<PaymentTransaction> transactions, int days)
    {
        var data = new List<HistoricalData>();
        var today = DateTime.UtcNow.Date;
        var transactionsByDate = transactions
            .GroupBy(t => (t.CompletedAt ?? t.CreatedAt).Date)
            .ToDictionary(g => g.Key, g => new
            {
                Revenue = g.Sum(t => t.Amount),
                Products = g.Count()
            });

        for (int i = days - 1; i >= 0; i--)
        {
            var date = today.AddDays(-i);
            transactionsByDate.TryGetValue(date, out var totals);

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
            existing.Status = machine.Status;
            existing.StockLevel = machine.StockLevel;
            existing.Revenue30d = machine.Revenue30d;
            existing.TotalSales30d = machine.TotalSales30d;
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
            _context.SaveChanges();
        }
    }

    private static string? NormalizeSerial(string? serial)
    {
        var normalized = serial?.Trim().ToUpperInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
