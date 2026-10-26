using Microsoft.EntityFrameworkCore;
using TechStore.Database;
using TechStore.Database.Entities;
using TechStore.Dto;
using TechStore.Services.Exceptions;

namespace TechStore.Services;

/// <summary>Order reads shared by the services that return or process orders.</summary>
internal static class OrderQueries
{
    public static Task<long> GetWarehouseIdAsync(TechStoreDbContext db, long orderId) =>
        db.Orders.AsNoTracking().Where(o => o.OrderId == orderId).Select(o => o.WarehouseId).SingleAsync();

    /// <summary>
    /// The order's lines sorted by product. Every operation that updates stock rows for several products
    /// (reserve, release, ship) must use this order: two orders then lock shared products in the same
    /// sequence and can never wait on each other in a circle (a deadlock).
    /// </summary>
    public static Task<List<OrderItem>> GetItemsInLockOrderAsync(TechStoreDbContext db, long orderId) =>
        db.OrderItems.AsNoTracking().Where(i => i.OrderId == orderId).OrderBy(i => i.ProductId).ToListAsync();

    public static async Task<OrderResponse> LoadResponseAsync(TechStoreDbContext db, long orderId)
    {
        // One SELECT joining orders, order_items and products. No tracking: we only read.
        var order = await db.Orders.AsNoTracking()
            .Include(o => o.Items).ThenInclude(i => i.Product)
            .SingleOrDefaultAsync(o => o.OrderId == orderId)
            ?? throw new NotFoundException($"Order {orderId} was not found.");

        var items = order.Items
            .OrderBy(i => i.OrderItemId)
            .Select(i => new OrderItemResponse(i.ProductId, i.Product.Sku, i.Product.Name, i.Quantity, i.UnitPrice))
            .ToList();

        return new OrderResponse(
            order.OrderId,
            order.CustomerId,
            order.WarehouseId,
            order.Status.ToString(),
            order.CreatedAt,
            items.Sum(i => i.Quantity),
            items.Sum(i => i.Quantity * i.UnitPrice),
            items);
    }
}
