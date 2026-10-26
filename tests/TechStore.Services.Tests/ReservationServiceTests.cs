using Microsoft.Extensions.Logging;
using TechStore.Database.Entities;
using TechStore.Services.Exceptions;
using TechStore.Services.Tests.Infrastructure;

namespace TechStore.Services.Tests;

public class ReservationServiceTests : DatabaseTestBase
{
    public ReservationServiceTests(DatabaseFixture fixture) : base(fixture)
    {
    }

    private async Task<T> RunAsync<T>(Func<ReservationService, Task<T>> action, ListLogger<ReservationService>? logger = null)
    {
        await using var db = NewContext();
        return await action(new ReservationService(db, logger ?? new ListLogger<ReservationService>()));
    }

    /// <summary>A customer, a warehouse and one product with 10 units on hand: the blueprint's "10 laptops".</summary>
    private async Task<(long Customer, long Warehouse, long Laptop)> SeedLaptopsAsync(int onHand = 10, int reserved = 0)
    {
        var customer = await SeedCustomerAsync();
        var warehouse = await SeedWarehouseAsync();
        var laptop = await SeedProductAsync("LAP-1", price: 1000m, name: "Laptop");
        await SeedBalanceAsync(laptop, warehouse, onHand, reserved);
        return (customer, warehouse, laptop);
    }

    // ======================================================================================================
    // SubmitOrder
    // ======================================================================================================

    [Fact]
    public async Task SubmitOrder_ReservesStock_MarksOrderReserved_AndLogsReservationMovement()
    {
        var (customer, warehouse, laptop) = await SeedLaptopsAsync();
        var orderId = await SeedOrderAsync(customer, warehouse, OrderStatus.Draft, (laptop, 2, 1000m));

        var response = await RunAsync(s => s.SubmitOrderAsync(orderId));

        Assert.Equal("Reserved", response.Status);
        Assert.Equal(orderId, response.OrderId);
        Assert.Equal((10, 2), await GetBalanceAsync(laptop, warehouse)); // 10 on hand, 2 reserved, 8 available
        Assert.Equal(OrderStatus.Reserved, await GetOrderStatusAsync(orderId));

        var movement = Assert.Single(await GetMovementsAsync());
        Assert.Equal(MovementType.Reservation, movement.Type);
        Assert.Equal(2, movement.QuantityChange);
        Assert.Equal(orderId, movement.OrderId);
        Assert.Equal((laptop, warehouse), (movement.ProductId, movement.WarehouseId));
    }

    [Fact]
    public async Task SubmitOrder_MultipleLines_ReservesEveryLine()
    {
        var (customer, warehouse, laptop) = await SeedLaptopsAsync();
        var mouse = await SeedProductAsync("MOU-1");
        await SeedBalanceAsync(mouse, warehouse, onHand: 5);
        var orderId = await SeedOrderAsync(customer, warehouse, OrderStatus.Draft, (laptop, 2, 1000m), (mouse, 5, 20m));

        await RunAsync(s => s.SubmitOrderAsync(orderId));

        Assert.Equal((10, 2), await GetBalanceAsync(laptop, warehouse));
        Assert.Equal((5, 5), await GetBalanceAsync(mouse, warehouse));
        Assert.Equal(2, (await GetMovementsAsync()).Count);
    }

    [Fact]
    public async Task SubmitOrder_ExactlyTheAvailableQuantity_Succeeds()
    {
        var (customer, warehouse, laptop) = await SeedLaptopsAsync(onHand: 10, reserved: 3);
        var orderId = await SeedOrderAsync(customer, warehouse, OrderStatus.Draft, (laptop, 7, 1000m));

        await RunAsync(s => s.SubmitOrderAsync(orderId));

        Assert.Equal((10, 10), await GetBalanceAsync(laptop, warehouse)); // available is now 0
    }

    [Fact]
    public async Task SubmitOrder_OneMoreThanAvailable_ThrowsConflict_AndChangesNothing()
    {
        var (customer, warehouse, laptop) = await SeedLaptopsAsync(onHand: 10, reserved: 3);
        var orderId = await SeedOrderAsync(customer, warehouse, OrderStatus.Draft, (laptop, 8, 1000m));

        await Assert.ThrowsAsync<ConflictException>(() => RunAsync(s => s.SubmitOrderAsync(orderId)));

        Assert.Equal((10, 3), await GetBalanceAsync(laptop, warehouse));
        Assert.Equal(OrderStatus.Draft, await GetOrderStatusAsync(orderId));
        Assert.Empty(await GetMovementsAsync());
    }

    [Fact]
    public async Task SubmitOrder_InsufficientStock_MessageNamesSkuRequestedAndAvailable()
    {
        var (customer, warehouse, laptop) = await SeedLaptopsAsync(onHand: 10);
        var orderId = await SeedOrderAsync(customer, warehouse, OrderStatus.Draft, (laptop, 15, 1000m));

        var ex = await Assert.ThrowsAsync<ConflictException>(() => RunAsync(s => s.SubmitOrderAsync(orderId)));

        Assert.Contains("LAP-1", ex.Message);
        Assert.Contains("15", ex.Message);
        Assert.Contains("10", ex.Message);
    }

    [Fact]
    public async Task SubmitOrder_OnlyTheLastLineLacksStock_ReservesNoLineAtAll()
    {
        var (customer, warehouse, laptop) = await SeedLaptopsAsync();
        var mouse = await SeedProductAsync("MOU-1");
        await SeedBalanceAsync(mouse, warehouse, onHand: 1);
        var orderId = await SeedOrderAsync(customer, warehouse, OrderStatus.Draft, (laptop, 2, 1000m), (mouse, 5, 20m));

        await Assert.ThrowsAsync<ConflictException>(() => RunAsync(s => s.SubmitOrderAsync(orderId)));

        Assert.Equal((10, 0), await GetBalanceAsync(laptop, warehouse)); // the laptop line was rolled back
        Assert.Equal((1, 0), await GetBalanceAsync(mouse, warehouse));
        Assert.Equal(OrderStatus.Draft, await GetOrderStatusAsync(orderId));
        Assert.Empty(await GetMovementsAsync());
    }

    [Fact]
    public async Task SubmitOrder_ProductHasNoBalanceInTheOrdersWarehouse_ThrowsConflict()
    {
        var (customer, _, laptop) = await SeedLaptopsAsync();
        var otherWarehouse = await SeedWarehouseAsync("Seattle", "Seattle, WA"); // stock exists only in Dallas
        var orderId = await SeedOrderAsync(customer, otherWarehouse, OrderStatus.Draft, (laptop, 1, 1000m));

        await Assert.ThrowsAsync<ConflictException>(() => RunAsync(s => s.SubmitOrderAsync(orderId)));

        Assert.Equal(OrderStatus.Draft, await GetOrderStatusAsync(orderId));
    }

    [Theory]
    [InlineData(OrderStatus.Reserved)]
    [InlineData(OrderStatus.Picking)]
    [InlineData(OrderStatus.Packed)]
    [InlineData(OrderStatus.Shipped)]
    [InlineData(OrderStatus.Canceled)]
    public async Task SubmitOrder_OrderNotInDraft_ThrowsConflict_AndLeavesStockAndStatusAlone(OrderStatus status)
    {
        var (customer, warehouse, laptop) = await SeedLaptopsAsync();
        var orderId = await SeedOrderAsync(customer, warehouse, status, (laptop, 2, 1000m));

        await Assert.ThrowsAsync<ConflictException>(() => RunAsync(s => s.SubmitOrderAsync(orderId)));

        Assert.Equal((10, 0), await GetBalanceAsync(laptop, warehouse));
        Assert.Equal(status, await GetOrderStatusAsync(orderId));
        Assert.Empty(await GetMovementsAsync());
    }

    [Fact]
    public async Task SubmitOrder_Twice_ReservesOnlyOnce()
    {
        var (customer, warehouse, laptop) = await SeedLaptopsAsync();
        var orderId = await SeedOrderAsync(customer, warehouse, OrderStatus.Draft, (laptop, 2, 1000m));
        await RunAsync(s => s.SubmitOrderAsync(orderId));

        await Assert.ThrowsAsync<ConflictException>(() => RunAsync(s => s.SubmitOrderAsync(orderId)));

        Assert.Equal((10, 2), await GetBalanceAsync(laptop, warehouse));
        Assert.Single(await GetMovementsAsync());
    }

    [Fact]
    public async Task SubmitOrder_UnknownOrder_ThrowsNotFound()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => RunAsync(s => s.SubmitOrderAsync(999)));
    }

    [Fact]
    public async Task SubmitOrder_NonPositiveId_IsRejected()
    {
        await Assert.ThrowsAsync<ValidationFailedException>(() => RunAsync(s => s.SubmitOrderAsync(0)));
    }

    [Fact]
    public async Task SubmitOrder_HundredConcurrentOrdersForTenUnits_NeverOversells()
    {
        var (customer, warehouse, laptop) = await SeedLaptopsAsync(onHand: 10);
        var orderIds = new List<long>();
        for (var i = 0; i < 100; i++)
        {
            orderIds.Add(await SeedOrderAsync(customer, warehouse, OrderStatus.Draft, (laptop, 1, 1000m)));
        }

        var attempts = orderIds.Select(id => Task.Run(async () =>
        {
            try
            {
                await RunAsync(s => s.SubmitOrderAsync(id));
                return true;
            }
            catch (ConflictException)
            {
                return false;
            }
        }));
        var results = await Task.WhenAll(attempts);

        Assert.Equal(10, results.Count(r => r));
        Assert.Equal((10, 10), await GetBalanceAsync(laptop, warehouse)); // reserved never exceeds on hand
        Assert.Equal(10, (await GetMovementsAsync()).Count);
    }

    [Fact]
    public async Task SubmitOrder_TwoMultiLineOrdersOverlappingProducts_BothCompleteWithoutDeadlock()
    {
        var customer = await SeedCustomerAsync();
        var warehouse = await SeedWarehouseAsync();
        var a = await SeedProductAsync("A-1");
        var b = await SeedProductAsync("B-1");
        await SeedBalanceAsync(a, warehouse, onHand: 100);
        await SeedBalanceAsync(b, warehouse, onHand: 100);

        var orderIds = new List<long>();
        for (var i = 0; i < 20; i++)
        {
            // Lines are listed in opposite orders; the service must still lock products in a fixed order.
            orderIds.Add(await SeedOrderAsync(customer, warehouse, OrderStatus.Draft,
                i % 2 == 0 ? new[] { (a, 1, 1m), (b, 1, 1m) } : new[] { (b, 1, 1m), (a, 1, 1m) }));
        }

        await Task.WhenAll(orderIds.Select(id => Task.Run(() => RunAsync(s => s.SubmitOrderAsync(id)))));

        Assert.Equal((100, 20), await GetBalanceAsync(a, warehouse));
        Assert.Equal((100, 20), await GetBalanceAsync(b, warehouse));
    }

    [Fact]
    public async Task SubmitOrder_FailureWhileReservingSecondLine_RollsBackFirstLineAndStatus()
    {
        var (customer, warehouse, laptop) = await SeedLaptopsAsync();
        var mouse = await SeedProductAsync("MOU-1");
        await SeedBalanceAsync(mouse, warehouse, onHand: 10);
        var orderId = await SeedOrderAsync(customer, warehouse, OrderStatus.Draft, (laptop, 2, 1000m), (mouse, 2, 20m));

        await using (await FailOnMovementInsertAsync(MovementType.Reservation, productId: mouse))
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(s => s.SubmitOrderAsync(orderId)));
            Assert.Contains("forced test failure", ex.ToString());
        }

        Assert.Equal((10, 0), await GetBalanceAsync(laptop, warehouse));
        Assert.Equal((10, 0), await GetBalanceAsync(mouse, warehouse));
        Assert.Equal(OrderStatus.Draft, await GetOrderStatusAsync(orderId));
        Assert.Empty(await GetMovementsAsync());
    }

    [Fact]
    public async Task SubmitOrder_WhenPostgresReportsADeadlock_IsReportedAsRetryableConflict_AndNothingChanges()
    {
        var (customer, warehouse, laptop) = await SeedLaptopsAsync();
        var orderId = await SeedOrderAsync(customer, warehouse, OrderStatus.Draft, (laptop, 2, 1000m));

        // SQLSTATE 40P01 is what PostgreSQL raises when it aborts a transaction to break a deadlock.
        await using (await FailOnMovementInsertAsync(MovementType.Reservation, sqlState: "40P01"))
        {
            var ex = await Assert.ThrowsAsync<ConflictException>(() => RunAsync(s => s.SubmitOrderAsync(orderId)));
            Assert.Contains("try again", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(OrderStatus.Draft, await GetOrderStatusAsync(orderId));
        Assert.Equal((10, 0), await GetBalanceAsync(laptop, warehouse));
    }

    [Fact]
    public async Task SubmitOrder_LogsOperationOutcomeAndOrderReference()
    {
        var (customer, warehouse, laptop) = await SeedLaptopsAsync();
        var orderId = await SeedOrderAsync(customer, warehouse, OrderStatus.Draft, (laptop, 2, 1000m));
        var logger = new ListLogger<ReservationService>();

        await RunAsync(s => s.SubmitOrderAsync(orderId), logger);
        await Assert.ThrowsAsync<ConflictException>(() => RunAsync(s => s.SubmitOrderAsync(orderId), logger));

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information
            && e.Message.Contains("SubmitOrder") && e.Message.Contains("succeeded") && e.Message.Contains($"order {orderId}"));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning
            && e.Message.Contains("SubmitOrder") && e.Message.Contains("rejected") && e.Message.Contains($"order {orderId}"));
    }

    // ======================================================================================================
    // CancelOrder
    // ======================================================================================================

    [Fact]
    public async Task CancelOrder_Draft_MarksCanceled_AndLeavesStockUntouched()
    {
        var (customer, warehouse, laptop) = await SeedLaptopsAsync();
        var orderId = await SeedOrderAsync(customer, warehouse, OrderStatus.Draft, (laptop, 2, 1000m));

        var response = await RunAsync(s => s.CancelOrderAsync(orderId));

        Assert.Equal("Canceled", response.Status);
        Assert.Equal(OrderStatus.Canceled, await GetOrderStatusAsync(orderId));
        Assert.Equal((10, 0), await GetBalanceAsync(laptop, warehouse));
        Assert.Empty(await GetMovementsAsync());
    }

    [Fact]
    public async Task CancelOrder_Reserved_ReleasesReservation_AndLogsReleaseMovement()
    {
        var (customer, warehouse, laptop) = await SeedLaptopsAsync(onHand: 10, reserved: 2);
        var orderId = await SeedOrderAsync(customer, warehouse, OrderStatus.Reserved, (laptop, 2, 1000m));

        var response = await RunAsync(s => s.CancelOrderAsync(orderId));

        Assert.Equal("Canceled", response.Status);
        Assert.Equal((10, 0), await GetBalanceAsync(laptop, warehouse)); // available is back to 10

        var movement = Assert.Single(await GetMovementsAsync());
        Assert.Equal(MovementType.Release, movement.Type);
        Assert.Equal(-2, movement.QuantityChange);
        Assert.Equal(orderId, movement.OrderId);
    }

    [Fact]
    public async Task CancelOrder_Reserved_ReleasesOnlyThisOrdersQuantity()
    {
        var (customer, warehouse, laptop) = await SeedLaptopsAsync(onHand: 10, reserved: 5); // 2 from this order, 3 from another
        var orderId = await SeedOrderAsync(customer, warehouse, OrderStatus.Reserved, (laptop, 2, 1000m));

        await RunAsync(s => s.CancelOrderAsync(orderId));

        Assert.Equal((10, 3), await GetBalanceAsync(laptop, warehouse));
    }

    [Fact]
    public async Task CancelOrder_ReservedWithSeveralLines_ReleasesEveryLine()
    {
        var (customer, warehouse, laptop) = await SeedLaptopsAsync(onHand: 10, reserved: 2);
        var mouse = await SeedProductAsync("MOU-1");
        await SeedBalanceAsync(mouse, warehouse, onHand: 5, reserved: 5);
        var orderId = await SeedOrderAsync(customer, warehouse, OrderStatus.Reserved, (laptop, 2, 1000m), (mouse, 5, 20m));

        await RunAsync(s => s.CancelOrderAsync(orderId));

        Assert.Equal((10, 0), await GetBalanceAsync(laptop, warehouse));
        Assert.Equal((5, 0), await GetBalanceAsync(mouse, warehouse));
        Assert.Equal(2, (await GetMovementsAsync()).Count);
    }

    [Theory]
    [InlineData(OrderStatus.Picking)]
    [InlineData(OrderStatus.Packed)]
    [InlineData(OrderStatus.Shipped)]
    [InlineData(OrderStatus.Canceled)]
    public async Task CancelOrder_NotDraftOrReserved_ThrowsConflict_AndChangesNothing(OrderStatus status)
    {
        var (customer, warehouse, laptop) = await SeedLaptopsAsync(onHand: 10, reserved: 2);
        var orderId = await SeedOrderAsync(customer, warehouse, status, (laptop, 2, 1000m));

        await Assert.ThrowsAsync<ConflictException>(() => RunAsync(s => s.CancelOrderAsync(orderId)));

        Assert.Equal((10, 2), await GetBalanceAsync(laptop, warehouse));
        Assert.Equal(status, await GetOrderStatusAsync(orderId));
        Assert.Empty(await GetMovementsAsync());
    }

    [Fact]
    public async Task CancelOrder_Twice_ReleasesOnlyOnce()
    {
        var (customer, warehouse, laptop) = await SeedLaptopsAsync(onHand: 10, reserved: 2);
        var orderId = await SeedOrderAsync(customer, warehouse, OrderStatus.Reserved, (laptop, 2, 1000m));
        await RunAsync(s => s.CancelOrderAsync(orderId));

        await Assert.ThrowsAsync<ConflictException>(() => RunAsync(s => s.CancelOrderAsync(orderId)));

        Assert.Equal((10, 0), await GetBalanceAsync(laptop, warehouse));
        Assert.Single(await GetMovementsAsync());
    }

    [Fact]
    public async Task CancelOrder_TwoSimultaneousCancels_ReleaseOnlyOnce()
    {
        var (customer, warehouse, laptop) = await SeedLaptopsAsync(onHand: 10, reserved: 2);
        var orderId = await SeedOrderAsync(customer, warehouse, OrderStatus.Reserved, (laptop, 2, 1000m));

        var attempts = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            try
            {
                await RunAsync(s => s.CancelOrderAsync(orderId));
                return true;
            }
            catch (ConflictException)
            {
                return false;
            }
        }));
        var results = await Task.WhenAll(attempts);

        Assert.Equal(1, results.Count(r => r));
        Assert.Equal((10, 0), await GetBalanceAsync(laptop, warehouse));
        Assert.Single(await GetMovementsAsync());
    }

    [Fact]
    public async Task CancelOrder_UnknownOrder_ThrowsNotFound()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => RunAsync(s => s.CancelOrderAsync(999)));
    }

    [Fact]
    public async Task CancelOrder_NonPositiveId_IsRejected()
    {
        await Assert.ThrowsAsync<ValidationFailedException>(() => RunAsync(s => s.CancelOrderAsync(-1)));
    }

    [Fact]
    public async Task CancelOrder_FailureWhileReleasing_LeavesOrderReservedAndStockUnchanged()
    {
        var (customer, warehouse, laptop) = await SeedLaptopsAsync(onHand: 10, reserved: 2);
        var orderId = await SeedOrderAsync(customer, warehouse, OrderStatus.Reserved, (laptop, 2, 1000m));

        await using (await FailOnMovementInsertAsync(MovementType.Release))
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(s => s.CancelOrderAsync(orderId)));
            Assert.Contains("forced test failure", ex.ToString());
        }

        Assert.Equal(OrderStatus.Reserved, await GetOrderStatusAsync(orderId));
        Assert.Equal((10, 2), await GetBalanceAsync(laptop, warehouse));
        Assert.Empty(await GetMovementsAsync());
    }

    [Fact]
    public async Task SubmitThenCancel_RestoresOriginalAvailability()
    {
        var (customer, warehouse, laptop) = await SeedLaptopsAsync();
        var orderId = await SeedOrderAsync(customer, warehouse, OrderStatus.Draft, (laptop, 2, 1000m));

        await RunAsync(s => s.SubmitOrderAsync(orderId));
        Assert.Equal((10, 2), await GetBalanceAsync(laptop, warehouse));
        await RunAsync(s => s.CancelOrderAsync(orderId));

        Assert.Equal((10, 0), await GetBalanceAsync(laptop, warehouse));
    }

    [Fact]
    public async Task CancelOrder_LogsOperationOutcomeAndOrderReference()
    {
        var (customer, warehouse, laptop) = await SeedLaptopsAsync();
        var orderId = await SeedOrderAsync(customer, warehouse, OrderStatus.Draft, (laptop, 2, 1000m));
        var logger = new ListLogger<ReservationService>();

        await RunAsync(s => s.CancelOrderAsync(orderId), logger);
        await Assert.ThrowsAsync<ConflictException>(() => RunAsync(s => s.CancelOrderAsync(orderId), logger));

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information
            && e.Message.Contains("CancelOrder") && e.Message.Contains("succeeded") && e.Message.Contains($"order {orderId}"));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning
            && e.Message.Contains("CancelOrder") && e.Message.Contains("rejected"));
    }
}
