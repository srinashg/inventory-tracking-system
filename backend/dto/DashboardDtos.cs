namespace TechStore.Dto;

public record LowStockItemResponse(long ProductId, string Sku, string Name, long WarehouseId, int Available);

public record DashboardSummaryResponse(
    long TotalAvailableUnits,
    int LowStockThreshold,
    int LowStockCount,
    IReadOnlyList<LowStockItemResponse> LowStockItems,
    int OpenOrders,
    int ShippedOrders);
