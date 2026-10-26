namespace TechStore.Database.Entities;

/// <summary>The person or company an order is for.</summary>
public class Customer
{
    public long CustomerId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;

    public ICollection<Order> Orders { get; set; } = new List<Order>();
}
