using VendingMachines.Api.Models;

namespace VendingMachines.Api.Services;

public interface IMockDataService
{
    List<Machine> GetMachines();
    List<Client> GetClients();
    List<Product> GetTopProducts();
    List<Product> GetBottomProducts();
    List<FullProduct> GetProducts();
    List<ProductType> GetProductTypes();
    DashboardStats GetDashboardStats();
}
