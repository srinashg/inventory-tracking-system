using Microsoft.EntityFrameworkCore;
using TechStore.Database.Entities;
using TechStore.Dto;
using TechStore.Services.Exceptions;
using TechStore.Services.Tests.Infrastructure;

namespace TechStore.Services.Tests;

public class OrderServiceTests : DatabaseTestBase
{
    public OrderServiceTests(DatabaseFixture fixture) : base(fixture)
    {
    }

    private async Task<T> RunAsync<T>(Func<OrderService, Task<T>> action)
    {
        await using var db = NewContext();
        return await action(new OrderService(db));
    }

    private async Task<int> CountOrdersAsync()
    {
        await using var db = NewContext();
        return await db.Orders.CountAsync();
    }

    private async Task<int> CountOrderItemsAsync()
    {
        await using var db = NewContext();
        return await db.OrderItems.CountAsync();
    }

    private static CreateOrderRequest OrderFor(long customerId, long warehouseId, params (long ProductId, int Quantity)[] lines) =>
        new(customerId, warehouseId, lines.Select(l => new OrderLineRequest(l.ProductId, l.Quantity)).ToList());

    // ======================================================================================================
    // CreateDraftOrder
    // ======================================================================================================

    [Fact]
    public async Task CreateDraftOrder_SavesDraftWithItemsAndCopiedPrices()
    {
        var customer = await SeedCustomerAsync();
        var warehouse = await SeedWarehouseAsync();
        var laptop = await SeedProductAsync("LAP-1", price: 1299.99m, name: "Laptop");

        var response = await RunAsync(s => s.CreateDraftOrderAsync(OrderFor(customer, warehouse, (laptop, 2))));

        Assert.True(response.OrderId > 0);
        Assert.Equal("Draft", response.Status);
        Assert.Equal((customer, warehouse), (response.CustomerId, response.WarehouseId));
        Assert.Equal(2, response.TotalUnits);
        Assert.Equal(2599.98m, response.TotalAmount);
        Assert.True(response.CreatedAt > DateTimeOffset.UtcNow.AddMinutes(-5));

        var item = Assert.Single(response.Items);
        Assert.Equal((laptop, "LAP-1", "Laptop", 2, 1299.99m), (item.ProductId, item.Sku, item.Name, item.Quantity, item.UnitPrice));
        Assert.Equal(OrderStatus.Draft, await GetOrderStatusAsync(response.OrderId));
    }

    [Fact]
    public async Task CreateDraftOrder_MultipleLines_TotalsAreSummed()
    {
        var customer = await SeedCustomerAsync();
        var warehouse = await SeedWarehouseAsync();
        var laptop = await SeedProductAsync(price: 1000m);
        var mouse = await SeedProductAsync(price: 25.50m);

        var response = await RunAsync(s => s.CreateDraftOrderAsync(OrderFor(customer, warehouse, (laptop, 2), (mouse, 4))));

        Assert.Equal(6, response.TotalUnits);
        Assert.Equal(2102m, response.TotalAmount);
        Assert.Equal(2, response.Items.Count);
    }

    [Fact]
    public async Task CreateDraftOrder_DoesNotChangeStock_OrWriteMovements()
    {
        var customer = await SeedCustomerAsync();
        var warehouse = await SeedWarehouseAsync();
        var laptop = await SeedProductAsync();
        await SeedBalanceAsync(laptop, warehouse, onHand: 10);

        await RunAsync(s => s.CreateDraftOrderAsync(OrderFor(customer, warehouse, (laptop, 2))));

        Assert.Equal((10, 0), await GetBalanceAsync(laptop, warehouse));
        Assert.Empty(await GetMovementsAsync());
    }

    [Fact]
    public async Task CreateDraftOrder_OrderingMoreThanIsInStock_StillSucceeds()
    {
        var customer = await SeedCustomerAsync();
        var warehouse = await SeedWarehouseAsync();
        var laptop = await SeedProductAsync();
        await SeedBalanceAsync(laptop, warehouse, onHand: 1);

        var response = await RunAsync(s => s.CreateDraftOrderAsync(OrderFor(customer, warehouse, (laptop, 50))));

        Assert.Equal("Draft", response.Status); // stock is only checked when the order is submitted
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(100, true)]
    [InlineData(0, false)]
    [InlineData(101, false)]
    public async Task CreateDraftOrder_LineCountBoundary(int lineCount, bool allowed)
    {
        var customer = await SeedCustomerAsync();
        var warehouse = await SeedWarehouseAsync();
        var lines = new List<(long, int)>();
        for (var i = 0; i < lineCount; i++)
        {
            lines.Add((await SeedProductAsync(), 1));
        }

        var request = OrderFor(customer, warehouse, lines.ToArray());

        if (allowed)
        {
            var response = await RunAsync(s => s.CreateDraftOrderAsync(request));
            Assert.Equal(lineCount, response.Items.Count);
        }
        else
        {
            await Assert.ThrowsAsync<ValidationFailedException>(() => RunAsync(s => s.CreateDraftOrderAsync(request)));
            Assert.Equal(0, await CountOrdersAsync());
        }
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(1_000_000, true)]
    [InlineData(0, false)]
    [InlineData(-3, false)]
    [InlineData(1_000_001, false)]
    public async Task CreateDraftOrder_QuantityBoundary(int quantity, bool allowed)
    {
        var customer = await SeedCustomerAsync();
        var warehouse = await SeedWarehouseAsync();
        var product = await SeedProductAsync();
        var request = OrderFor(customer, warehouse, (product, quantity));

        if (allowed)
        {
            var response = await RunAsync(s => s.CreateDraftOrderAsync(request));
            Assert.Equal(quantity, response.TotalUnits);
        }
        else
        {
            await Assert.ThrowsAsync<ValidationFailedException>(() => RunAsync(s => s.CreateDraftOrderAsync(request)));
            Assert.Equal(0, await CountOrdersAsync());
        }
    }

    [Fact]
    public async Task CreateDraftOrder_SameProductOnTwoLines_IsRejected()
    {
        var customer = await SeedCustomerAsync();
        var warehouse = await SeedWarehouseAsync();
        var product = await SeedProductAsync();

        await Assert.ThrowsAsync<ValidationFailedException>(() =>
            RunAsync(s => s.CreateDraftOrderAsync(OrderFor(customer, warehouse, (product, 1), (product, 2)))));

        Assert.Equal(0, await CountOrdersAsync());
    }

    [Fact]
    public async Task CreateDraftOrder_MissingIdsOrItems_AreRejected()
    {
        await Assert.ThrowsAsync<ValidationFailedException>(() =>
            RunAsync(s => s.CreateDraftOrderAsync(OrderFor(0, 0, (0, 1)))));
        await Assert.ThrowsAsync<ValidationFailedException>(() =>
            RunAsync(s => s.CreateDraftOrderAsync(new CreateOrderRequest(1, 1, null!))));
    }

    [Fact]
    public async Task CreateDraftOrder_UnknownCustomerWarehouseOrProduct_ThrowsNotFound_AndSavesNothing()
    {
        var customer = await SeedCustomerAsync();
        var warehouse = await SeedWarehouseAsync();
        var product = await SeedProductAsync();

        await Assert.ThrowsAsync<NotFoundException>(() =>
            RunAsync(s => s.CreateDraftOrderAsync(OrderFor(999, warehouse, (product, 1)))));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            RunAsync(s => s.CreateDraftOrderAsync(OrderFor(customer, 999, (product, 1)))));
        // A missing product on the second line must discard the whole order, including the valid first line.
        await Assert.ThrowsAsync<NotFoundException>(() =>
            RunAsync(s => s.CreateDraftOrderAsync(OrderFor(customer, warehouse, (product, 1), (999, 1)))));

        Assert.Equal(0, await CountOrdersAsync());
        Assert.Equal(0, await CountOrderItemsAsync());
    }

    [Fact]
    public async Task CreateDraftOrder_InactiveProduct_ThrowsConflictNamingTheSku_AndSavesNothing()
    {
        var customer = await SeedCustomerAsync();
        var warehouse = await SeedWarehouseAsync();
        var active = await SeedProductAsync("ACTIVE-1");
        var inactive = await SeedProductAsync("OLD-1", isActive: false);

        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            RunAsync(s => s.CreateDraftOrderAsync(OrderFor(customer, warehouse, (active, 1), (inactive, 1)))));

        Assert.Contains("OLD-1", ex.Message);
        Assert.Equal(0, await CountOrdersAsync());
        Assert.Equal(0, await CountOrderItemsAsync());
    }

    [Fact]
    public async Task CreateDraftOrder_LaterPriceChange_DoesNotAlterSavedUnitPrice()
    {
        var customer = await SeedCustomerAsync();
        var warehouse = await SeedWarehouseAsync();
        var product = await SeedProductAsync(price: 100m);
        var created = await RunAsync(s => s.CreateDraftOrderAsync(OrderFor(customer, warehouse, (product, 1))));

        await using (var db = NewContext())
        {
            var p = await db.Products.SingleAsync(x => x.ProductId == product);
            p.Price = 175m;
            await db.SaveChangesAsync();
        }

        var reloaded = await RunAsync(s => s.GetOrderAsync(created.OrderId));
        Assert.Equal(100m, reloaded.Items[0].UnitPrice);
        Assert.Equal(100m, reloaded.TotalAmount);
    }

    // ======================================================================================================
    // GetOrder
    // ======================================================================================================

    [Fact]
    public async Task GetOrder_ReturnsItemsAndCurrentStatus()
    {
        var customer = await SeedCustomerAsync();
        var warehouse = await SeedWarehouseAsync();
        var laptop = await SeedProductAsync("LAP-1", name: "Laptop");
        var mouse = await SeedProductAsync("MOU-1", name: "Mouse");
        var orderId = await SeedOrderAsync(customer, warehouse, OrderStatus.Packed, (laptop, 2, 900m), (mouse, 1, 20m));

        var response = await RunAsync(s => s.GetOrderAsync(orderId));

        Assert.Equal(orderId, response.OrderId);
        Assert.Equal("Packed", response.Status);
        Assert.Equal(3, response.TotalUnits);
        Assert.Equal(1820m, response.TotalAmount);
        Assert.Equal(new[] { "LAP-1", "MOU-1" }, response.Items.Select(i => i.Sku));
    }

    [Fact]
    public async Task GetOrder_WithMaximumHundredLines_ReturnsAllOfThem()
    {
        var customer = await SeedCustomerAsync();
        var warehouse = await SeedWarehouseAsync();
        var lines = new List<(long, int, decimal)>();
        for (var i = 0; i < 100; i++)
        {
            lines.Add((await SeedProductAsync(), 1, 10m));
        }

        var orderId = await SeedOrderAsync(customer, warehouse, OrderStatus.Draft, lines.ToArray());

        var response = await RunAsync(s => s.GetOrderAsync(orderId));

        Assert.Equal(100, response.Items.Count);
        Assert.Equal(100, response.TotalUnits);
    }

    [Fact]
    public async Task GetOrder_UnknownId_ThrowsNotFound()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => RunAsync(s => s.GetOrderAsync(999)));
    }

    [Fact]
    public async Task GetOrder_NonPositiveId_IsRejected()
    {
        await Assert.ThrowsAsync<ValidationFailedException>(() => RunAsync(s => s.GetOrderAsync(0)));
    }

    // ======================================================================================================
    // ListOrders
    // ======================================================================================================

    [Fact]
    public async Task ListOrders_ReturnsOrdersNewestFirst_WithTotals()
    {
        var customer = await SeedCustomerAsync();
        var warehouse = await SeedWarehouseAsync();
        var laptop = await SeedProductAsync();
        var mouse = await SeedProductAsync();
        var first = await SeedOrderAsync(customer, warehouse, OrderStatus.Draft, (laptop, 2, 1000m));
        var second = await SeedOrderAsync(customer, warehouse, OrderStatus.Reserved, (laptop, 1, 1000m), (mouse, 4, 25.5m));

        var page = await RunAsync(s => s.ListOrdersAsync());

        Assert.Equal(new[] { second, first }, page.Items.Select(o => o.OrderId));
        Assert.Equal((1, 50, 2), (page.Page, page.PageSize, page.TotalCount));
        var newest = page.Items[0];
        Assert.Equal((customer, warehouse, "Reserved", 5, 1102m), (newest.CustomerId, newest.WarehouseId, newest.Status, newest.TotalUnits, newest.TotalAmount));
        Assert.True(newest.CreatedAt > DateTimeOffset.UtcNow.AddMinutes(-5));
        Assert.Equal((2, 2000m), (page.Items[1].TotalUnits, page.Items[1].TotalAmount));
    }

    [Fact]
    public async Task ListOrders_NoOrders_ReturnsEmptyPage()
    {
        var page = await RunAsync(s => s.ListOrdersAsync());

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
    }

    [Fact]
    public async Task ListOrders_FilterByOneStatus_ReturnsOnlyThoseOrders()
    {
        var (customer, warehouse, product) = await SeedBasicsAsync();
        await SeedOrderAsync(customer, warehouse, OrderStatus.Draft, (product, 1, 10m));
        var reserved = await SeedOrderAsync(customer, warehouse, OrderStatus.Reserved, (product, 1, 10m));
        await SeedOrderAsync(customer, warehouse, OrderStatus.Shipped, (product, 1, 10m));

        var page = await RunAsync(s => s.ListOrdersAsync(statuses: new[] { "Reserved" }));

        Assert.Equal(new[] { reserved }, page.Items.Select(o => o.OrderId));
        Assert.Equal(1, page.TotalCount);
    }

    [Fact]
    public async Task ListOrders_FilterByStatusIgnoresCase()
    {
        var (customer, warehouse, product) = await SeedBasicsAsync();
        var packed = await SeedOrderAsync(customer, warehouse, OrderStatus.Packed, (product, 1, 10m));

        var page = await RunAsync(s => s.ListOrdersAsync(statuses: new[] { "pAcKeD" }));

        Assert.Equal(new[] { packed }, page.Items.Select(o => o.OrderId));
    }

    [Fact]
    public async Task ListOrders_SeveralStatuses_AreOrTogether()
    {
        var (customer, warehouse, product) = await SeedBasicsAsync();
        await SeedOrderAsync(customer, warehouse, OrderStatus.Draft, (product, 1, 10m));
        var reserved = await SeedOrderAsync(customer, warehouse, OrderStatus.Reserved, (product, 1, 10m));
        var picking = await SeedOrderAsync(customer, warehouse, OrderStatus.Picking, (product, 1, 10m));
        var packed = await SeedOrderAsync(customer, warehouse, OrderStatus.Packed, (product, 1, 10m));
        await SeedOrderAsync(customer, warehouse, OrderStatus.Canceled, (product, 1, 10m));

        var page = await RunAsync(s => s.ListOrdersAsync(statuses: new[] { "Reserved", "Picking", "Packed" }));

        Assert.Equal(new[] { packed, picking, reserved }, page.Items.Select(o => o.OrderId));
    }

    [Fact]
    public async Task ListOrders_FilterByCustomerAndWarehouse_AreAndedTogether()
    {
        var ann = await SeedCustomerAsync("Ann");
        var bob = await SeedCustomerAsync("Bob");
        var dallas = await SeedWarehouseAsync("Dallas");
        var seattle = await SeedWarehouseAsync("Seattle");
        var product = await SeedProductAsync();
        await SeedOrderAsync(ann, dallas, OrderStatus.Draft, (product, 1, 10m));
        var annSeattle = await SeedOrderAsync(ann, seattle, OrderStatus.Draft, (product, 1, 10m));
        await SeedOrderAsync(bob, seattle, OrderStatus.Draft, (product, 1, 10m));

        var byCustomer = await RunAsync(s => s.ListOrdersAsync(customerId: ann));
        var byWarehouse = await RunAsync(s => s.ListOrdersAsync(warehouseId: seattle));
        var both = await RunAsync(s => s.ListOrdersAsync(customerId: ann, warehouseId: seattle));

        Assert.Equal(2, byCustomer.TotalCount);
        Assert.Equal(2, byWarehouse.TotalCount);
        Assert.Equal(new[] { annSeattle }, both.Items.Select(o => o.OrderId));
    }

    [Fact]
    public async Task ListOrders_Paging_ReturnsRequestedSlice_AndFullTotal()
    {
        var (customer, warehouse, product) = await SeedBasicsAsync();
        var ids = new List<long>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add(await SeedOrderAsync(customer, warehouse, OrderStatus.Draft, (product, 1, 10m)));
        }

        var page1 = await RunAsync(s => s.ListOrdersAsync(page: 1, pageSize: 2));
        var page3 = await RunAsync(s => s.ListOrdersAsync(page: 3, pageSize: 2));
        var page4 = await RunAsync(s => s.ListOrdersAsync(page: 4, pageSize: 2));

        Assert.Equal(new[] { ids[4], ids[3] }, page1.Items.Select(o => o.OrderId));
        Assert.Equal(new[] { ids[0] }, page3.Items.Select(o => o.OrderId));
        Assert.Empty(page4.Items);
        Assert.Equal(5, page4.TotalCount);
    }

    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(1, 200, true)]
    [InlineData(0, 50, false)]
    [InlineData(1, 0, false)]
    [InlineData(1, 201, false)]
    public async Task ListOrders_PagingBoundaries(int page, int pageSize, bool allowed)
    {
        if (allowed)
        {
            var result = await RunAsync(s => s.ListOrdersAsync(page: page, pageSize: pageSize));
            Assert.Equal(pageSize, result.PageSize);
        }
        else
        {
            await Assert.ThrowsAsync<ValidationFailedException>(() => RunAsync(s => s.ListOrdersAsync(page: page, pageSize: pageSize)));
        }
    }

    [Theory]
    [InlineData("Bogus")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("1")]
    [InlineData("Draft,Reserved")]
    public async Task ListOrders_UnknownStatus_IsRejected(string status)
    {
        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => RunAsync(s => s.ListOrdersAsync(statuses: new[] { status })));

        Assert.Contains("status", ex.Errors.Keys);
    }

    [Fact]
    public async Task ListOrders_OneBadStatusAmongGoodOnes_IsRejected()
    {
        await Assert.ThrowsAsync<ValidationFailedException>(() =>
            RunAsync(s => s.ListOrdersAsync(statuses: new[] { "Reserved", "Nope" })));
    }

    [Theory]
    [InlineData(0L, null)]
    [InlineData(-1L, null)]
    [InlineData(null, 0L)]
    [InlineData(null, -3L)]
    public async Task ListOrders_NonPositiveIds_AreRejected(long? customerId, long? warehouseId)
    {
        await Assert.ThrowsAsync<ValidationFailedException>(() =>
            RunAsync(s => s.ListOrdersAsync(customerId: customerId, warehouseId: warehouseId)));
    }

    [Fact]
    public async Task ListOrders_StatusFollowsEachTransition()
    {
        var (customer, warehouse, product) = await SeedBasicsAsync();
        await SeedBalanceAsync(product, warehouse, onHand: 10);
        var orderId = (await RunAsync(s => s.CreateDraftOrderAsync(OrderFor(customer, warehouse, (product, 2))))).OrderId;

        var statuses = new List<string> { (await RunAsync(s => s.ListOrdersAsync())).Items.Single().Status };
        await using (var db = NewContext())
        {
            await new ReservationService(db, new ListLogger<ReservationService>()).SubmitOrderAsync(orderId);
        }
        statuses.Add((await RunAsync(s => s.ListOrdersAsync())).Items.Single().Status);
        foreach (var step in new Func<FulfillmentService, Task<OrderResponse>>[] { f => f.PickOrderAsync(orderId), f => f.PackOrderAsync(orderId), f => f.ShipOrderAsync(orderId) })
        {
            await using var db = NewContext();
            await step(new FulfillmentService(db, new ListLogger<FulfillmentService>()));
            statuses.Add((await RunAsync(s => s.ListOrdersAsync())).Items.Single().Status);
        }

        Assert.Equal(new[] { "Draft", "Reserved", "Picking", "Packed", "Shipped" }, statuses);
    }

    [Fact]
    public async Task ListOrders_ChangesNothing()
    {
        var (customer, warehouse, product) = await SeedBasicsAsync();
        await SeedBalanceAsync(product, warehouse, onHand: 10, reserved: 2);
        var orderId = await SeedOrderAsync(customer, warehouse, OrderStatus.Reserved, (product, 2, 10m));

        await RunAsync(s => s.ListOrdersAsync());

        Assert.Equal(OrderStatus.Reserved, await GetOrderStatusAsync(orderId));
        Assert.Equal((10, 2), await GetBalanceAsync(product, warehouse));
        Assert.Empty(await GetMovementsAsync());
    }

    private async Task<(long Customer, long Warehouse, long Product)> SeedBasicsAsync() =>
        (await SeedCustomerAsync(), await SeedWarehouseAsync(), await SeedProductAsync());
}
