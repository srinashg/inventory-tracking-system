using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using TechStore.Database;
using TechStore.Database.Entities;
using TechStore.Dto;
using TechStore.Services.Exceptions;
using TechStore.Services.Validation;

namespace TechStore.Services;

/// <summary>Shows stock levels and records stock receipts and corrections.</summary>
public class InventoryService
{
    private readonly TechStoreDbContext _db;
    private readonly ILogger<InventoryService> _logger;

    public InventoryService(TechStoreDbContext db, ILogger<InventoryService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<PagedResponse<InventoryItemResponse>> GetInventoryAsync(long warehouseId, int page = 1, int pageSize = 50)
    {
        var errors = new ValidationErrors();
        Rules.Id(warehouseId, "warehouseId", errors);
        Rules.Paging(page, pageSize, errors);
        errors.ThrowIfAny();

        if (!await _db.Warehouses.AnyAsync(w => w.WarehouseId == warehouseId))
        {
            throw new NotFoundException($"Warehouse {warehouseId} was not found.");
        }

        var balances = _db.InventoryBalances.AsNoTracking().Where(b => b.WarehouseId == warehouseId);

        // SELECT COUNT(*), then one page of balances joined to their products. "Available" is calculated
        // here and never stored, so it can never disagree with on_hand and reserved.
        var totalCount = await balances.CountAsync();
        var items = await balances
            .OrderBy(b => b.Product.Sku)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(b => new InventoryItemResponse(
                b.ProductId, b.Product.Sku, b.Product.Name, b.OnHand, b.Reserved, b.OnHand - b.Reserved))
            .ToListAsync();

        return new PagedResponse<InventoryItemResponse>(items, page, pageSize, totalCount);
    }

    public Task<StockChangeResponse> ReceiveStockAsync(ReceiveStockRequest request) =>
        OperationLogger.RunAsync(_logger, "ReceiveStock", $"product {request.ProductId}, warehouse {request.WarehouseId}", async () =>
        {
            var errors = new ValidationErrors();
            Rules.Id(request.ProductId, "productId", errors);
            Rules.Id(request.WarehouseId, "warehouseId", errors);
            Rules.Quantity(request.Quantity, errors);
            var note = Rules.OptionalNote(request.Note, "note", errors);
            errors.ThrowIfAny();

            // BEGIN: the balance change and its movement record must succeed or fail together.
            await using var transaction = await _db.Database.BeginTransactionAsync();

            await EnsureProductAndWarehouseExistAsync(request.ProductId, request.WarehouseId);

            // One statement creates the balance on the first receipt, or adds to it afterwards. ON CONFLICT
            // lets two simultaneous "first" receipts both succeed instead of one failing on the primary key.
            try
            {
                await _db.Database.ExecuteSqlInterpolatedAsync($@"
                    INSERT INTO inventory_balances (product_id, warehouse_id, on_hand, reserved)
                    VALUES ({request.ProductId}, {request.WarehouseId}, {request.Quantity}, 0)
                    ON CONFLICT (product_id, warehouse_id)
                    DO UPDATE SET on_hand = inventory_balances.on_hand + {request.Quantity}");
            }
            catch (Exception ex) when (ex.HasSqlState(PostgresErrorCodes.NumericValueOutOfRange))
            {
                throw new ConflictException("Receiving this quantity would exceed the maximum stock that can be stored.");
            }

            var movement = new InventoryMovement
            {
                ProductId = request.ProductId,
                WarehouseId = request.WarehouseId,
                QuantityChange = request.Quantity,
                Type = MovementType.Receipt,
                Reason = note
            };
            _db.InventoryMovements.Add(movement);
            await _db.SaveChangesAsync(); // INSERT the movement inside the same transaction

            var response = await ReadBalanceAsync(movement.MovementId, request.ProductId, request.WarehouseId);
            await transaction.CommitAsync(); // COMMIT: both rows become visible together
            return response;
        });

    public Task<StockChangeResponse> AdjustStockAsync(AdjustStockRequest request) =>
        OperationLogger.RunAsync(_logger, "AdjustStock", $"product {request.ProductId}, warehouse {request.WarehouseId}", async () =>
        {
            var errors = new ValidationErrors();
            Rules.Id(request.ProductId, "productId", errors);
            Rules.Id(request.WarehouseId, "warehouseId", errors);
            Rules.SignedQuantity(request.QuantityChange, errors);
            var reason = Rules.RequiredText(request.Reason, "reason", errors);
            errors.ThrowIfAny();

            await using var transaction = await _db.Database.BeginTransactionAsync();

            await EnsureProductAndWarehouseExistAsync(request.ProductId, request.WarehouseId);

            // Check and change in ONE statement: on_hand may only move if it stays at or above reserved
            // (which also keeps it non-negative). Another request cannot slip in between a check and an update.
            var change = request.QuantityChange;
            int rowsChanged;
            try
            {
                rowsChanged = await _db.InventoryBalances
                    .Where(b => b.ProductId == request.ProductId
                             && b.WarehouseId == request.WarehouseId
                             && b.OnHand + change >= b.Reserved)
                    .ExecuteUpdateAsync(s => s.SetProperty(b => b.OnHand, b => b.OnHand + change));
            }
            catch (Exception ex) when (ex.HasSqlState(PostgresErrorCodes.NumericValueOutOfRange))
            {
                throw new ConflictException("Adjusting by this quantity would exceed the maximum stock that can be stored.");
            }

            if (rowsChanged == 0)
            {
                // Nothing matched: either there is no balance yet, or the change would break the stock rule.
                var balanceExists = await _db.InventoryBalances.AnyAsync(
                    b => b.ProductId == request.ProductId && b.WarehouseId == request.WarehouseId);

                throw balanceExists
                    ? new ConflictException("The adjustment would reduce on-hand stock below the quantity already reserved.")
                    : new NotFoundException("No stock exists for this product in this warehouse yet. Record a receipt first.");
            }

            var movement = new InventoryMovement
            {
                ProductId = request.ProductId,
                WarehouseId = request.WarehouseId,
                QuantityChange = change,
                Type = MovementType.Adjustment,
                Reason = reason
            };
            _db.InventoryMovements.Add(movement);
            await _db.SaveChangesAsync();

            var response = await ReadBalanceAsync(movement.MovementId, request.ProductId, request.WarehouseId);
            await transaction.CommitAsync();
            return response;
        });

    private async Task EnsureProductAndWarehouseExistAsync(long productId, long warehouseId)
    {
        if (!await _db.Products.AnyAsync(p => p.ProductId == productId))
        {
            throw new NotFoundException($"Product {productId} was not found.");
        }

        if (!await _db.Warehouses.AnyAsync(w => w.WarehouseId == warehouseId))
        {
            throw new NotFoundException($"Warehouse {warehouseId} was not found.");
        }
    }

    /// <summary>Reads the balance as this transaction sees it (including its own uncommitted changes).</summary>
    private async Task<StockChangeResponse> ReadBalanceAsync(long movementId, long productId, long warehouseId)
    {
        var balance = await _db.InventoryBalances.AsNoTracking()
            .SingleAsync(b => b.ProductId == productId && b.WarehouseId == warehouseId);

        return new StockChangeResponse(movementId, productId, warehouseId, balance.OnHand, balance.Reserved, balance.Available);
    }
}
