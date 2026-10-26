using Microsoft.EntityFrameworkCore;
using TechStore.Database;
using TechStore.Database.Entities;

namespace TechStore.Services.Tests.Infrastructure;

/// <summary>
/// Base class for service tests: empties every table before each test and offers seeding/inspection helpers.
/// Each helper uses its own short-lived DbContext so that tests never share tracked entities with the service.
/// </summary>
[Collection(DatabaseCollection.Name)]
public abstract class DatabaseTestBase : IAsyncLifetime
{
    private readonly DatabaseFixture _fixture;
    private int _skuCounter;

    protected DatabaseTestBase(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        await using var db = NewContext();
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE inventory_movements, order_items, orders, inventory_balances, products, warehouses, customers RESTART IDENTITY CASCADE");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    protected TechStoreDbContext NewContext() => _fixture.CreateContext();

    // ---- Seeding -----------------------------------------------------------------------------------------

    protected async Task<long> SeedProductAsync(string? sku = null, decimal price = 100m, bool isActive = true, string name = "Test product")
    {
        await using var db = NewContext();
        var product = new Product { Sku = sku ?? $"SKU-{++_skuCounter}", Name = name, Price = price, IsActive = isActive };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product.ProductId;
    }

    protected async Task<long> SeedWarehouseAsync(string name = "Dallas", string location = "Dallas, TX")
    {
        await using var db = NewContext();
        var warehouse = new Warehouse { Name = name, Location = location };
        db.Warehouses.Add(warehouse);
        await db.SaveChangesAsync();
        return warehouse.WarehouseId;
    }

    protected async Task<long> SeedCustomerAsync(string name = "Ann Lee", string email = "ann@example.com")
    {
        await using var db = NewContext();
        var customer = new Customer { Name = name, Email = email };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        return customer.CustomerId;
    }

    protected async Task SeedBalanceAsync(long productId, long warehouseId, int onHand, int reserved = 0)
    {
        await using var db = NewContext();
        db.InventoryBalances.Add(new InventoryBalance
        {
            ProductId = productId,
            WarehouseId = warehouseId,
            OnHand = onHand,
            Reserved = reserved
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Inserts an order directly in the given status, bypassing the services.</summary>
    protected async Task<long> SeedOrderAsync(
        long customerId,
        long warehouseId,
        OrderStatus status,
        params (long ProductId, int Quantity, decimal UnitPrice)[] lines)
    {
        await using var db = NewContext();
        var order = new Order { CustomerId = customerId, WarehouseId = warehouseId, Status = status };
        foreach (var (productId, quantity, unitPrice) in lines)
        {
            order.Items.Add(new OrderItem { ProductId = productId, Quantity = quantity, UnitPrice = unitPrice });
        }

        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order.OrderId;
    }

    // ---- Inspection --------------------------------------------------------------------------------------

    protected async Task<(int OnHand, int Reserved)?> GetBalanceAsync(long productId, long warehouseId)
    {
        await using var db = NewContext();
        var balance = await db.InventoryBalances.AsNoTracking()
            .SingleOrDefaultAsync(b => b.ProductId == productId && b.WarehouseId == warehouseId);
        return balance is null ? null : (balance.OnHand, balance.Reserved);
    }

    protected async Task<OrderStatus> GetOrderStatusAsync(long orderId)
    {
        await using var db = NewContext();
        return await db.Orders.AsNoTracking().Where(o => o.OrderId == orderId).Select(o => o.Status).SingleAsync();
    }

    protected async Task<List<InventoryMovement>> GetMovementsAsync()
    {
        await using var db = NewContext();
        return await db.InventoryMovements.AsNoTracking().OrderBy(m => m.MovementId).ToListAsync();
    }

    /// <summary>
    /// Makes PostgreSQL raise an error whenever a movement of the given type is inserted, to simulate a
    /// failure in the middle of a transaction. Dispose the returned object to remove the trigger.
    /// </summary>
    protected async Task<IAsyncDisposable> FailOnMovementInsertAsync(
        MovementType type, long? productId = null, string sqlState = "P0001")
    {
        // Only an enum name and a long are inserted into this test-only SQL, so injection is not a concern.
        var productFilter = productId is null ? "" : $" AND NEW.product_id = {productId}";
        var createTrigger = $@"
            CREATE OR REPLACE FUNCTION test_forced_failure() RETURNS trigger AS $$
            BEGIN
                IF NEW.type = '{type}'{productFilter} THEN
                    RAISE EXCEPTION 'forced test failure' USING ERRCODE = '{sqlState}';
                END IF;
                RETURN NEW;
            END $$ LANGUAGE plpgsql;
            CREATE TRIGGER test_forced_failure BEFORE INSERT ON inventory_movements
            FOR EACH ROW EXECUTE FUNCTION test_forced_failure();";

        await using (var db = NewContext())
        {
            await db.Database.ExecuteSqlRawAsync(createTrigger);
        }

        return new TriggerRemover(NewContext);
    }

    private sealed class TriggerRemover : IAsyncDisposable
    {
        private readonly Func<TechStoreDbContext> _newContext;

        public TriggerRemover(Func<TechStoreDbContext> newContext) => _newContext = newContext;

        public async ValueTask DisposeAsync()
        {
            await using var db = _newContext();
            await db.Database.ExecuteSqlRawAsync(
                "DROP TRIGGER IF EXISTS test_forced_failure ON inventory_movements; DROP FUNCTION IF EXISTS test_forced_failure();");
        }
    }
}
