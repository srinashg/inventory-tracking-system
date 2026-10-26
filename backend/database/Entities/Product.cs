namespace TechStore.Database.Entities;

/// <summary>A sellable item in the catalog.</summary>
public class Product
{
    public long ProductId { get; set; }
    public string Sku { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public bool IsActive { get; set; } = true;

    public ICollection<InventoryBalance> InventoryBalances { get; set; } = new List<InventoryBalance>();
    public ICollection<OrderItem> OrderItems { get; set; } = new List<OrderItem>();
}
