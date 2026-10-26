namespace TechStore.Database.Entities;

/// <summary>A physical place where stock is kept and orders are fulfilled from.</summary>
public class Warehouse
{
    public long WarehouseId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;

    public ICollection<InventoryBalance> InventoryBalances { get; set; } = new List<InventoryBalance>();
    public ICollection<Order> Orders { get; set; } = new List<Order>();
}
