namespace TechStore.Dto;

public record OrderLineRequest(long ProductId, int Quantity);

public record CreateOrderRequest(long CustomerId, long WarehouseId, IReadOnlyList<OrderLineRequest> Items);

public record OrderItemResponse(long ProductId, string Sku, string Name, int Quantity, decimal UnitPrice);

/// <summary>Returned by every order endpoint. Status is the enum name, for example "Draft".</summary>
public record OrderResponse(
    long OrderId,
    long CustomerId,
    long WarehouseId,
    string Status,
    DateTimeOffset CreatedAt,
    int TotalUnits,
    decimal TotalAmount,
    IReadOnlyList<OrderItemResponse> Items);

/// <summary>One row of the order list: an order without its lines. Status is the enum name, for example "Reserved".</summary>
public record OrderListItem(
    long OrderId,
    long CustomerId,
    long WarehouseId,
    string Status,
    DateTimeOffset CreatedAt,
    int TotalUnits,
    decimal TotalAmount);
