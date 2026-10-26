namespace TechStore.Database.Entities;

/// <summary>Append-only audit record of one change to stock. Never updated or deleted.</summary>
public class InventoryMovement
{
    public long MovementId { get; set; }
    public long ProductId { get; set; }
    public long WarehouseId { get; set; }

    /// <summary>Set for Reservation, Release and Shipment; null for Receipt and Adjustment.</summary>
    public long? OrderId { get; set; }

    /// <summary>Signed change to the quantity this type affects (OnHand or Reserved).</summary>
    public int QuantityChange { get; set; }

    public MovementType Type { get; set; }

    /// <summary>Required for Adjustment.</summary>
    public string? Reason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Product Product { get; set; } = null!;
    public Warehouse Warehouse { get; set; } = null!;
    public Order? Order { get; set; }
}
