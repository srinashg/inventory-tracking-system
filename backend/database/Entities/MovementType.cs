namespace TechStore.Database.Entities;

/// <summary>Why stock changed. Stored in the database as the enum name (for example "Receipt").</summary>
public enum MovementType
{
    /// <summary>New stock arrived; changes OnHand.</summary>
    Receipt,

    /// <summary>Manual correction with a reason; changes OnHand.</summary>
    Adjustment,

    /// <summary>Order submitted; changes Reserved (positive).</summary>
    Reservation,

    /// <summary>Reserved order canceled; changes Reserved (negative).</summary>
    Release,

    /// <summary>Order shipped; changes OnHand (negative) and releases the reservation.</summary>
    Shipment
}
