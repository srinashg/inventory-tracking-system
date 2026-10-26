using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TechStore.Database;
using TechStore.Database.Entities;
using TechStore.Dto;
using TechStore.Services.Validation;

namespace TechStore.Services;

/// <summary>Moves a reserved order through picking, packing and shipping; shipping deducts the stock.</summary>
public class FulfillmentService
{
    private readonly TechStoreDbContext _db;
    private readonly ILogger<FulfillmentService> _logger;

    public FulfillmentService(TechStoreDbContext db, ILogger<FulfillmentService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public Task<OrderResponse> PickOrderAsync(long orderId) =>
        AdvanceStatusAsync(orderId, OrderStatus.Reserved, OrderStatus.Picking);

    public Task<OrderResponse> PackOrderAsync(long orderId) =>
        AdvanceStatusAsync(orderId, OrderStatus.Picking, OrderStatus.Packed);

    public Task<OrderResponse> ShipOrderAsync(long orderId) =>
        OperationLogger.RunAsync(_logger, "ShipOrder", $"order {orderId}", async () =>
        {
            Rules.RequireId(orderId, "orderId");

            // BEGIN: status change, stock deduction and movement records succeed or fail together.
            await using var transaction = await _db.Database.BeginTransactionAsync();

            // Claim the order first (Packed -> Shipped). A repeated or racing ship request gets 0 rows here and
            // is refused BEFORE it touches stock, so the stock can never be deducted twice.
            await OrderStatusClaim.ClaimAsync(_db, orderId, OrderStatus.Packed, OrderStatus.Shipped);

            var warehouseId = await OrderQueries.GetWarehouseIdAsync(_db, orderId);

            foreach (var item in await OrderQueries.GetItemsInLockOrderAsync(_db, orderId))
            {
                var quantity = item.Quantity;

                // The units leave the building: on_hand and reserved both go down by the same amount, so
                // "available" (on_hand - reserved) does not change at the moment of shipping.
                var rowsChanged = await _db.InventoryBalances
                    .Where(b => b.ProductId == item.ProductId
                             && b.WarehouseId == warehouseId
                             && b.Reserved >= quantity
                             && b.OnHand >= quantity)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(b => b.OnHand, b => b.OnHand - quantity)
                        .SetProperty(b => b.Reserved, b => b.Reserved - quantity));

                if (rowsChanged == 0)
                {
                    // A Packed order must have its stock reserved; if not, the data is inconsistent. Roll back.
                    throw new InvalidOperationException(
                        $"Order {orderId} is Packed but product {item.ProductId} lacks {quantity} reserved units in warehouse {warehouseId}.");
                }

                _db.InventoryMovements.Add(new InventoryMovement
                {
                    ProductId = item.ProductId,
                    WarehouseId = warehouseId,
                    OrderId = orderId,
                    QuantityChange = -quantity,
                    Type = MovementType.Shipment
                });
            }

            await _db.SaveChangesAsync(); // INSERT the Shipment movements inside the same transaction
            await transaction.CommitAsync();

            return await OrderQueries.LoadResponseAsync(_db, orderId);
        });

    /// <summary>Pick and pack only change the status: one conditional UPDATE, atomic by itself.</summary>
    private async Task<OrderResponse> AdvanceStatusAsync(long orderId, OrderStatus from, OrderStatus to)
    {
        Rules.RequireId(orderId, "orderId");
        await OrderStatusClaim.ClaimAsync(_db, orderId, from, to);
        return await OrderQueries.LoadResponseAsync(_db, orderId);
    }
}
