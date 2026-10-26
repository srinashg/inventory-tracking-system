namespace TechStore.Database.Entities;

/// <summary>A request to ship products to a customer from one warehouse. Maps to the "orders" table.</summary>
public class Order
{
    public long OrderId { get; set; }
    public long CustomerId { get; set; }
    public long WarehouseId { get; set; }
    public OrderStatus Status { get; set; } = OrderStatus.Draft;
    public DateTimeOffset CreatedAt { get; set; }

    public Customer Customer { get; set; } = null!;
    public Warehouse Warehouse { get; set; } = null!;
    public ICollection<OrderItem> Items { get; set; } = new List<OrderItem>();
    public ICollection<InventoryMovement> Movements { get; set; } = new List<InventoryMovement>();
}
