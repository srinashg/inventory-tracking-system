using Microsoft.Extensions.Logging;
using TechStore.Database.Entities;
using TechStore.Services.Exceptions;
using TechStore.Services.Tests.Infrastructure;

namespace TechStore.Services.Tests;

public class FulfillmentServiceTests : DatabaseTestBase
{
    public FulfillmentServiceTests(DatabaseFixture fixture) : base(fixture)
    {
    }

    private async Task<T> RunAsync<T>(Func<FulfillmentService, Task<T>> action, ListLogger<FulfillmentService>? logger = null)
    {
        await using var db = NewContext();
        return await action(new FulfillmentService(db, logger ?? new ListLogger<FulfillmentService>()));
    }

    /// <summary>10 laptops on hand, 2 of them reserved, and an order for those 2 in the given status.</summary>
    private async Task<(long Customer, long Warehouse, long Laptop, long Order)> SeedReservedOrderAsync(OrderStatus status)
    {
        var customer = await SeedCustomerAsync();
        var warehouse = await SeedWarehouseAsync();
        var laptop = await SeedProductAsync("LAP-1", price: 1000m, name: "Laptop");
        await SeedBalanceAsync(laptop, warehouse, onHand: 10, reserved: 2);
        var orderId = await SeedOrderAsync(customer, warehouse, status, (laptop, 2, 1000m));
        return (customer, warehouse, laptop, orderId);
    }

    // ======================================================================================================
    // PickOrder
    // ======================================================================================================

    [Fact]
    public async Task PickOrder_ReservedOrder_BecomesPicking_WithStockUnchanged()
    {
        var (_, warehouse, laptop, orderId) = await SeedReservedOrderAsync(OrderStatus.Reserved);

        var response = await RunAsync(s => s.PickOrderAsync(orderId));

        Assert.Equal("Picking", response.Status);
        Assert.Equal(OrderStatus.Picking, await GetOrderStatusAsync(orderId));
        Assert.Equal((10, 2), await GetBalanceAsync(laptop, warehouse));
        Assert.Empty(await GetMovementsAsync());
    }

    [Theory]
    [InlineData(OrderStatus.Draft)]
    [InlineData(OrderStatus.Picking)]
    [InlineData(OrderStatus.Packed)]
    [InlineData(OrderStatus.Shipped)]
    [InlineData(OrderStatus.Canceled)]
    public async Task PickOrder_NotReserved_ThrowsConflict_AndKeepsStatus(OrderStatus status)
    {
        var (_, _, _, orderId) = await SeedReservedOrderAsync(status);

        await Assert.ThrowsAsync<ConflictException>(() => RunAsync(s => s.PickOrderAsync(orderId)));

        Assert.Equal(status, await GetOrderStatusAsync(orderId));
    }

    [Fact]
    public async Task PickOrder_TwoSimultaneousRequests_ExactlyOneSucceeds()
    {
        var (_, _, _, orderId) = await SeedReservedOrderAsync(OrderStatus.Reserved);

        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            try
            {
                await RunAsync(s => s.PickOrderAsync(orderId));
                return true;
            }
            catch (ConflictException)
            {
                return false;
            }
        })));

        Assert.Equal(1, results.Count(r => r));
    }

    [Fact]
    public async Task PickOrder_UnknownOrder_ThrowsNotFound()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => RunAsync(s => s.PickOrderAsync(999)));
    }

    [Fact]
    public async Task PickOrder_NonPositiveId_IsRejected()
    {
        await Assert.ThrowsAsync<ValidationFailedException>(() => RunAsync(s => s.PickOrderAsync(0)));
    }

    // ======================================================================================================
    // PackOrder
    // ======================================================================================================

    [Fact]
    public async Task PackOrder_PickingOrder_BecomesPacked_WithStockUnchanged()
    {
        var (_, warehouse, laptop, orderId) = await SeedReservedOrderAsync(OrderStatus.Picking);

        var response = await RunAsync(s => s.PackOrderAsync(orderId));

        Assert.Equal("Packed", response.Status);
        Assert.Equal(OrderStatus.Packed, await GetOrderStatusAsync(orderId));
        Assert.Equal((10, 2), await GetBalanceAsync(laptop, warehouse));
    }

    [Theory]
    [InlineData(OrderStatus.Draft)]
    [InlineData(OrderStatus.Reserved)]
    [InlineData(OrderStatus.Packed)]
    [InlineData(OrderStatus.Shipped)]
    [InlineData(OrderStatus.Canceled)]
    public async Task PackOrder_NotPicking_ThrowsConflict_AndKeepsStatus(OrderStatus status)
    {
        var (_, _, _, orderId) = await SeedReservedOrderAsync(status);

        await Assert.ThrowsAsync<ConflictException>(() => RunAsync(s => s.PackOrderAsync(orderId)));

        Assert.Equal(status, await GetOrderStatusAsync(orderId));
    }

    [Fact]
    public async Task PackOrder_UnknownOrder_ThrowsNotFound()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => RunAsync(s => s.PackOrderAsync(999)));
    }

    [Fact]
    public async Task PackOrder_NonPositiveId_IsRejected()
    {
        await Assert.ThrowsAsync<ValidationFailedException>(() => RunAsync(s => s.PackOrderAsync(-1)));
    }

    // ======================================================================================================
    // ShipOrder
    // ======================================================================================================

    [Fact]
    public async Task ShipOrder_PackedOrder_DeductsOnHandAndReserved_AndLogsShipmentMovement()
    {
        var (_, warehouse, laptop, orderId) = await SeedReservedOrderAsync(OrderStatus.Packed);

        var response = await RunAsync(s => s.ShipOrderAsync(orderId));

        Assert.Equal("Shipped", response.Status);
        Assert.Equal(OrderStatus.Shipped, await GetOrderStatusAsync(orderId));
        Assert.Equal((8, 0), await GetBalanceAsync(laptop, warehouse)); // 8 on hand, 0 reserved, 8 available

        var movement = Assert.Single(await GetMovementsAsync());
        Assert.Equal(MovementType.Shipment, movement.Type);
        Assert.Equal(-2, movement.QuantityChange);
        Assert.Equal(orderId, movement.OrderId);
        Assert.Equal((laptop, warehouse), (movement.ProductId, movement.WarehouseId));
    }

    [Fact]
    public async Task ShipOrder_ShippingAllStock_LeavesZeroOnHandAndZeroReserved()
    {
        var customer = await SeedCustomerAsync();
        var warehouse = await SeedWarehouseAsync();
        var laptop = await SeedProductAsync();
        await SeedBalanceAsync(laptop, warehouse, onHand: 10, reserved: 10);
        var orderId = await SeedOrderAsync(customer, warehouse, OrderStatus.Packed, (laptop, 10, 1000m));

        await RunAsync(s => s.ShipOrderAsync(orderId));

        Assert.Equal((0, 0), await GetBalanceAsync(laptop, warehouse));
    }

    [Fact]
    public async Task ShipOrder_OnlyDeductsThisOrdersQuantity_NotOtherReservations()
    {
        var customer = await SeedCustomerAsync();
        var warehouse = await SeedWarehouseAsync();
        var laptop = await SeedProductAsync();
        await SeedBalanceAsync(laptop, warehouse, onHand: 10, reserved: 5); // 2 belong to this order, 3 to another
        var orderId = await SeedOrderAsync(customer, warehouse, OrderStatus.Packed, (laptop, 2, 1000m));

        await RunAsync(s => s.ShipOrderAsync(orderId));

        Assert.Equal((8, 3), await GetBalanceAsync(laptop, warehouse));
    }

    [Fact]
    public async Task ShipOrder_MultipleLines_DeductsEveryLine()
    {
        var (customer, warehouse, laptop, _) = await SeedReservedOrderAsync(OrderStatus.Draft);
        var mouse = await SeedProductAsync("MOU-1");
        await SeedBalanceAsync(mouse, warehouse, onHand: 5, reserved: 3);
        var orderId = await SeedOrderAsync(customer, warehouse, OrderStatus.Packed, (laptop, 2, 1000m), (mouse, 3, 20m));

        await RunAsync(s => s.ShipOrderAsync(orderId));

        Assert.Equal((8, 0), await GetBalanceAsync(laptop, warehouse));
        Assert.Equal((2, 0), await GetBalanceAsync(mouse, warehouse));
        Assert.Equal(2, (await GetMovementsAsync()).Count);
    }

    [Theory]
    [InlineData(OrderStatus.Draft)]
    [InlineData(OrderStatus.Reserved)]
    [InlineData(OrderStatus.Picking)]
    [InlineData(OrderStatus.Shipped)]
    [InlineData(OrderStatus.Canceled)]
    public async Task ShipOrder_NotPacked_ThrowsConflict_AndChangesNothing(OrderStatus status)
    {
        var (_, warehouse, laptop, orderId) = await SeedReservedOrderAsync(status);

        await Assert.ThrowsAsync<ConflictException>(() => RunAsync(s => s.ShipOrderAsync(orderId)));

        Assert.Equal(status, await GetOrderStatusAsync(orderId));
        Assert.Equal((10, 2), await GetBalanceAsync(laptop, warehouse));
        Assert.Empty(await GetMovementsAsync());
    }

    [Fact]
    public async Task ShipOrder_Twice_DeductsOnlyOnce()
    {
        var (_, warehouse, laptop, orderId) = await SeedReservedOrderAsync(OrderStatus.Packed);
        await RunAsync(s => s.ShipOrderAsync(orderId));

        await Assert.ThrowsAsync<ConflictException>(() => RunAsync(s => s.ShipOrderAsync(orderId)));

        Assert.Equal((8, 0), await GetBalanceAsync(laptop, warehouse));
        Assert.Single(await GetMovementsAsync());
    }

    [Fact]
    public async Task ShipOrder_TwentySimultaneousRequests_DeductExactlyOnce()
    {
        var (_, warehouse, laptop, orderId) = await SeedReservedOrderAsync(OrderStatus.Packed);

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(async () =>
        {
            try
            {
                await RunAsync(s => s.ShipOrderAsync(orderId));
                return true;
            }
            catch (ConflictException)
            {
                return false;
            }
        })));

        Assert.Equal(1, results.Count(r => r));
        Assert.Equal((8, 0), await GetBalanceAsync(laptop, warehouse));
        Assert.Single(await GetMovementsAsync());
    }

    [Fact]
    public async Task ShipOrder_UnknownOrder_ThrowsNotFound()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => RunAsync(s => s.ShipOrderAsync(999)));
    }

    [Fact]
    public async Task ShipOrder_NonPositiveId_IsRejected()
    {
        await Assert.ThrowsAsync<ValidationFailedException>(() => RunAsync(s => s.ShipOrderAsync(0)));
    }

    [Fact]
    public async Task ShipOrder_FailureOnSecondLine_RollsBackFirstLineMovementsAndStatus()
    {
        var (customer, warehouse, laptop, _) = await SeedReservedOrderAsync(OrderStatus.Draft);
        var mouse = await SeedProductAsync("MOU-1");
        await SeedBalanceAsync(mouse, warehouse, onHand: 5, reserved: 3);
        var orderId = await SeedOrderAsync(customer, warehouse, OrderStatus.Packed, (laptop, 2, 1000m), (mouse, 3, 20m));

        await using (await FailOnMovementInsertAsync(MovementType.Shipment, productId: mouse))
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(s => s.ShipOrderAsync(orderId)));
            Assert.Contains("forced test failure", ex.ToString());
        }

        Assert.Equal(OrderStatus.Packed, await GetOrderStatusAsync(orderId));
        Assert.Equal((10, 2), await GetBalanceAsync(laptop, warehouse));
        Assert.Equal((5, 3), await GetBalanceAsync(mouse, warehouse));
        Assert.Empty(await GetMovementsAsync());
    }

    [Fact]
    public async Task ShipOrder_WhenReservedStockIsMissing_RollsBack_AndThrows()
    {
        // Inconsistent data (a Packed order whose stock is not reserved) must never produce negative stock.
        var customer = await SeedCustomerAsync();
        var warehouse = await SeedWarehouseAsync();
        var laptop = await SeedProductAsync();
        await SeedBalanceAsync(laptop, warehouse, onHand: 10, reserved: 0);
        var orderId = await SeedOrderAsync(customer, warehouse, OrderStatus.Packed, (laptop, 2, 1000m));

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(s => s.ShipOrderAsync(orderId)));

        Assert.Equal(OrderStatus.Packed, await GetOrderStatusAsync(orderId));
        Assert.Equal((10, 0), await GetBalanceAsync(laptop, warehouse));
    }

    [Fact]
    public async Task ShipOrder_LogsOperationOutcomeAndOrderReference()
    {
        var (_, _, _, orderId) = await SeedReservedOrderAsync(OrderStatus.Packed);
        var logger = new ListLogger<FulfillmentService>();

        await RunAsync(s => s.ShipOrderAsync(orderId), logger);
        await Assert.ThrowsAsync<ConflictException>(() => RunAsync(s => s.ShipOrderAsync(orderId), logger));

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information
            && e.Message.Contains("ShipOrder") && e.Message.Contains("succeeded") && e.Message.Contains($"order {orderId}"));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning
            && e.Message.Contains("ShipOrder") && e.Message.Contains("rejected"));
    }

    // ======================================================================================================
    // The whole journey
    // ======================================================================================================

    [Fact]
    public async Task FullJourney_Receive10_Reserve2_Pick_Pack_Ship_Leaves8Available()
    {
        // Uses the real services end to end, mirroring the blueprint's final acceptance scenario.
        var customer = await SeedCustomerAsync();
        var warehouse = await SeedWarehouseAsync();
        var laptop = await SeedProductAsync("LAP-1", price: 1000m);

        await using (var db = NewContext())
        {
            await new InventoryService(db, new ListLogger<InventoryService>())
                .ReceiveStockAsync(new TechStore.Dto.ReceiveStockRequest(laptop, warehouse, 10, null));
        }

        long orderId;
        await using (var db = NewContext())
        {
            var draft = await new OrderService(db).CreateDraftOrderAsync(new TechStore.Dto.CreateOrderRequest(
                customer, warehouse, new[] { new TechStore.Dto.OrderLineRequest(laptop, 2) }));
            orderId = draft.OrderId;
        }

        await using (var db = NewContext())
        {
            await new ReservationService(db, new ListLogger<ReservationService>()).SubmitOrderAsync(orderId);
        }
        Assert.Equal((10, 2), await GetBalanceAsync(laptop, warehouse));

        await RunAsync(s => s.PickOrderAsync(orderId));
        await RunAsync(s => s.PackOrderAsync(orderId));
        var shipped = await RunAsync(s => s.ShipOrderAsync(orderId));

        Assert.Equal("Shipped", shipped.Status);
        Assert.Equal((8, 0), await GetBalanceAsync(laptop, warehouse)); // 8 on hand, 8 available
        Assert.Equal(
            new[] { MovementType.Receipt, MovementType.Reservation, MovementType.Shipment },
            (await GetMovementsAsync()).Select(m => m.Type));
    }
}
