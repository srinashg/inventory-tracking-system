namespace TechStore.Database.Entities;

/// <summary>Current stock of one product in one warehouse. The pair (ProductId, WarehouseId) is the key.</summary>
public class InventoryBalance
{
    public long ProductId { get; set; }
    public long WarehouseId { get; set; }

    /// <summary>Units physically in the warehouse.</summary>
    public int OnHand { get; set; }

    /// <summary>Units promised to submitted orders.</summary>
    public int Reserved { get; set; }

    /// <summary>Calculated, never stored: units that can still be promised.</summary>
    public int Available => OnHand - Reserved;

    public Product Product { get; set; } = null!;
    public Warehouse Warehouse { get; set; } = null!;
}
