namespace TechStore.Dto;

public record ReceiveStockRequest(long ProductId, long WarehouseId, int Quantity, string? Note);

/// <summary>QuantityChange is signed: positive adds stock, negative removes it.</summary>
public record AdjustStockRequest(long ProductId, long WarehouseId, int QuantityChange, string Reason);

public record InventoryItemResponse(long ProductId, string Sku, string Name, int OnHand, int Reserved, int Available);

public record StockChangeResponse(long MovementId, long ProductId, long WarehouseId, int OnHand, int Reserved, int Available);
