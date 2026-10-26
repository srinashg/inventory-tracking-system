namespace TechStore.Database.Entities;

/// <summary>Where an order is in its lifecycle. Stored in the database as the enum name (for example "Draft").</summary>
public enum OrderStatus
{
    Draft,
    Reserved,
    Picking,
    Packed,
    Shipped,
    Canceled
}
