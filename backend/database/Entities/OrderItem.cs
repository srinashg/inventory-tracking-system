namespace TechStore.Database.Entities;

/// <summary>One product line on an order.</summary>
public class OrderItem
{
    public long OrderItemId { get; set; }
    public long OrderId { get; set; }
    public long ProductId { get; set; }
    public int Quantity { get; set; }

    /// <summary>Product price copied when the order is created; later price changes do not affect it.</summary>
    public decimal UnitPrice { get; set; }

    public Order Order { get; set; } = null!;
    public Product Product { get; set; } = null!;
}
