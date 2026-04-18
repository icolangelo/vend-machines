namespace VendingMachines.Api.Models;

public class FullProduct
{
    public string Id { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string TypeId { get; set; } = string.Empty;
    public bool IsAlcoholic { get; set; }
    public decimal Cost { get; set; }
}
