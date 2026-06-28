using VendingMachines.Api.Models;

namespace VendingMachines.Api.Services;

public interface IDataService
{
    List<Machine> GetMachines(Guid? companyId = null);
    List<Client> GetClients(Guid? companyId = null);
    List<Product> GetTopProducts();
    List<Product> GetBottomProducts();
    List<FullProduct> GetProducts();
    List<ProductType> GetProductTypes();
    DashboardStats GetDashboardStats(Guid? companyId = null);
    void AddMachine(Machine machine);
    void UpdateMachine(Machine machine);
}
