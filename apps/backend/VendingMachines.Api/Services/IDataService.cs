using VendingMachines.Api.Models;

namespace VendingMachines.Api.Services;

public interface IDataService
{
    List<Machine> GetMachines(Guid? companyId = null);
    List<LocationDto> GetLocations(Guid? companyId = null);
    List<Product> GetTopProducts(Guid? companyId = null);
    List<Product> GetBottomProducts(Guid? companyId = null);
    List<FullProduct> GetProducts(Guid? companyId = null);
    List<ProductType> GetProductTypes(Guid? companyId = null);
    DashboardStats GetDashboardStats(Guid? companyId = null);
    void AddMachine(Machine machine);
    void UpdateMachine(Machine machine);
    FullProduct AddProduct(FullProduct product);
    FullProduct UpdateProduct(FullProduct product);
    ProductType AddProductType(ProductType productType);
    ProductType UpdateProductType(ProductType productType);
}
