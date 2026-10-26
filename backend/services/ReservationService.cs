using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using TechStore.Database;
using TechStore.Database.Entities;
using TechStore.Dto;
using TechStore.Services.Exceptions;
using TechStore.Services.Validation;

namespace TechStore.Services;

/// <summary>Reserves stock when an order is submitted and releases it when an order is canceled.</summary>
public class ReservationService
{
    private readonly TechStoreDbContext _db;
    private readonly ILogger<ReservationService> _logger;

    public ReservationService(TechStoreDbContext db, ILogger<ReservationService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public Task<OrderResponse> SubmitOrderAsync(long orderId) =>
        OperationLogger.RunAsync(_logger, "SubmitOrder", $"order {orderId}", async () =>
        {
            Rules.RequireId(orderId, "orderId");

            // BEGIN: the status change, every reservation and every movement record succeed or fail together.
            await using var transaction = await _db.Database.BeginTransactionAsync();

            // Claim the order first (Draft -> Reserved). A second submit of the same order stops here.
            await OrderStatusClaim.ClaimAsync(_db, orderId, OrderStatus.Draft, OrderStatus.Reserved);

            var warehouseId = await OrderQueries.GetWarehouseIdAsync(_db, orderId);

            foreach (var item in await OrderQueries.GetItemsInLockOrderAsync(_db, orderId))
            {
                var quantity = item.Quantity;

                // Check and reserve in ONE statement: reserved only increases if enough is still available.
                // If another request took the stock first, this row matches nothing (0 rows changed).
                var rowsChanged = await _db.InventoryBalances
                    .Where(b => b.ProductId == item.ProductId
                             && b.WarehouseId == warehouseId
                             && b.OnHand - b.Reserved >= quantity)
                    .ExecuteUpdateAsync(s => s.SetProperty(b => b.Reserved, b => b.Reserved + quantity));

                if (rowsChanged == 0)
                {
                    await ThrowInsufficientStockAsync(transaction, item, warehouseId);
                }

                _db.InventoryMovements.Add(new InventoryMovement
                {
                    ProductId = item.ProductId,
                    WarehouseId = warehouseId,
                    OrderId = orderId,
                    QuantityChange = quantity,
                    Type = MovementType.Reservation
                });
            }

            await _db.SaveChangesAsync(); // INSERT the movement rows inside the same transaction
            await transaction.CommitAsync(); // COMMIT: order status, reservations and movements appear together

            return await OrderQueries.LoadResponseAsync(_db, orderId);
        });

    public Task<OrderResponse> CancelOrderAsync(long orderId) =>
        OperationLogger.RunAsync(_logger, "CancelOrder", $"order {orderId}", async () =>
        {
            Rules.RequireId(orderId, "orderId");

            await using var transaction = await _db.Database.BeginTransactionAsync();

            var order = await _db.Orders.AsNoTracking()
                .Where(o => o.OrderId == orderId)
                .Select(o => new { o.Status, o.WarehouseId })
                .SingleOrDefaultAsync()
                ?? throw new NotFoundException($"Order {orderId} was not found.");

            if (order.Status is not (OrderStatus.Draft or OrderStatus.Reserved))
            {
                throw new ConflictException($"Order {orderId} is {order.Status} and can no longer be canceled.");
            }

            // Claim the order, but only if it still has the status we just read. If another request changed it
            // in the meantime this matches 0 rows and we are refused, so a reservation is never released twice.
            await OrderStatusClaim.ClaimAsync(_db, orderId, order.Status, OrderStatus.Canceled);

            if (order.Status == OrderStatus.Reserved)
            {
                await ReleaseReservationAsync(orderId, order.WarehouseId);
            }

            await _db.SaveChangesAsync(); // INSERT the Release movements inside the same transaction
            await transaction.CommitAsync();

            return await OrderQueries.LoadResponseAsync(_db, orderId);
        });

    /// <summary>Gives each line's reserved quantity back (reserved goes down, so available goes up).</summary>
    private async Task ReleaseReservationAsync(long orderId, long warehouseId)
    {
        foreach (var item in await OrderQueries.GetItemsInLockOrderAsync(_db, orderId))
        {
            var quantity = item.Quantity;

            var rowsChanged = await _db.InventoryBalances
                .Where(b => b.ProductId == item.ProductId && b.WarehouseId == warehouseId && b.Reserved >= quantity)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.Reserved, b => b.Reserved - quantity));

            if (rowsChanged == 0)
            {
                // A Reserved order must always have its stock reserved; if not, the data is inconsistent. Roll back.
                throw new InvalidOperationException(
                    $"Order {orderId} is Reserved but product {item.ProductId} has less than {quantity} reserved in warehouse {warehouseId}.");
            }

            _db.InventoryMovements.Add(new InventoryMovement
            {
                ProductId = item.ProductId,
                WarehouseId = warehouseId,
                OrderId = orderId,
                QuantityChange = -quantity,
                Type = MovementType.Release
            });
        }
    }

    /// <summary>Undoes the transaction (so the order is still Draft), then reports which line was short.</summary>
    private async Task ThrowInsufficientStockAsync(IDbContextTransaction transaction, OrderItem item, long warehouseId)
    {
        await transaction.RollbackAsync(); // ROLLBACK: earlier lines and the status change are undone

        var sku = await _db.Products.AsNoTracking()
            .Where(p => p.ProductId == item.ProductId).Select(p => p.Sku).SingleAsync();
        var available = await _db.InventoryBalances.AsNoTracking()
            .Where(b => b.ProductId == item.ProductId && b.WarehouseId == warehouseId)
            .Select(b => b.OnHand - b.Reserved)
            .SingleOrDefaultAsync();

        throw new ConflictException(
            $"Insufficient stock for SKU {sku}: requested {item.Quantity}, available {available}.");
    }
}
