using Microsoft.EntityFrameworkCore;
using TechStore.Database;
using TechStore.Database.Entities;
using TechStore.Dto;

namespace TechStore.Services;

/// <summary>Builds the one-screen summary of stock and orders.</summary>
public class DashboardService
{
    /// <summary>A product is "low stock" in a warehouse when its available quantity is at or below this.</summary>
    public const int LowStockThreshold = 5;

    public const int MaxLowStockItems = 50;

    private readonly TechStoreDbContext _db;

    public DashboardService(TechStoreDbContext db)
    {
        _db = db;
    }

    public async Task<DashboardSummaryResponse> GetSummaryAsync()
    {
        // Three read-only queries. They are not wrapped in one transaction: a summary may differ by a
        // moment between queries, which is acceptable here.

        // SELECT SUM(on_hand - reserved) FROM inventory_balances (summed as long: many rows could exceed int).
        var totalAvailable = await _db.InventoryBalances.AsNoTracking()
            .SumAsync(b => (long)(b.OnHand - b.Reserved));

        // Active products whose available quantity is at or below the threshold in some warehouse.
        var lowStock = _db.InventoryBalances.AsNoTracking()
            .Where(b => b.Product.IsActive && b.OnHand - b.Reserved <= LowStockThreshold);

        var lowStockCount = await lowStock.CountAsync();
        var lowStockItems = await lowStock
            .OrderBy(b => b.OnHand - b.Reserved)
            .ThenBy(b => b.Product.Sku)
            .ThenBy(b => b.WarehouseId)
            .Take(MaxLowStockItems)
            .Select(b => new LowStockItemResponse(b.ProductId, b.Product.Sku, b.Product.Name, b.WarehouseId, b.OnHand - b.Reserved))
            .ToListAsync();

        // SELECT status, COUNT(*) FROM orders GROUP BY status; the two totals are added up in memory.
        var ordersByStatus = await _db.Orders.AsNoTracking()
            .GroupBy(o => o.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Status, x => x.Count);

        int CountOf(params OrderStatus[] statuses) =>
            statuses.Sum(s => ordersByStatus.GetValueOrDefault(s));

        var openOrders = CountOf(OrderStatus.Draft, OrderStatus.Reserved, OrderStatus.Picking, OrderStatus.Packed);
        var shippedOrders = CountOf(OrderStatus.Shipped);

        return new DashboardSummaryResponse(
            totalAvailable, LowStockThreshold, lowStockCount, lowStockItems, openOrders, shippedOrders);
    }
}
