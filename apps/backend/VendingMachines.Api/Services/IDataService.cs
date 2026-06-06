using VendingMachines.Api.Models;

namespace VendingMachines.Api.Services;

public interface IDataService
{
    List<Machine> GetMachines();
    List<Client> GetClients();
    List<Product> GetTopProducts();
    List<Product> GetBottomProducts();
    List<FullProduct> GetProducts();
    List<ProductType> GetProductTypes();
    DashboardStats GetDashboardStats();
    void AddMachine(Machine machine);
    void UpdateMachine(Machine machine);
}
