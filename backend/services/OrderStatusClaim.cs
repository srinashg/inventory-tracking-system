using Microsoft.EntityFrameworkCore;
using TechStore.Database;
using TechStore.Database.Entities;
using TechStore.Services.Exceptions;

namespace TechStore.Services;

/// <summary>
/// Moves an order from one status to the next with a single conditional UPDATE
/// ("... WHERE order_id = @id AND status = @from"). Whichever request gets the row wins; a duplicate or racing
/// request matches 0 rows and is refused before it can touch stock. This enforces the transition table in docs/services.md.
/// </summary>
internal static class OrderStatusClaim
{
    public static async Task ClaimAsync(TechStoreDbContext db, long orderId, OrderStatus from, OrderStatus to)
    {
        var rowsChanged = await db.Orders
            .Where(o => o.OrderId == orderId && o.Status == from)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, to));

        if (rowsChanged == 1)
        {
            return;
        }

        // 0 rows: the order is missing, or it is not in the expected status. Look up which, to report it clearly.
        var current = await db.Orders.AsNoTracking()
            .Where(o => o.OrderId == orderId)
            .Select(o => (OrderStatus?)o.Status)
            .SingleOrDefaultAsync();

        throw current is null
            ? new NotFoundException($"Order {orderId} was not found.")
            : new ConflictException($"Order {orderId} is {current}; it must be {from} to become {to}.");
    }
}
