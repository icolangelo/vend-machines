namespace VendingMachines.Api.Models;

public class ProductType
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public Guid? CompanyId { get; set; }
    public Company? Company { get; set; }
    public string? OriginalId { get; set; }
}
