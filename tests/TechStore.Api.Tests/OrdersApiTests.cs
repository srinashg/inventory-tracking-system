using System.Net;
using TechStore.Api.Tests.Infrastructure;
using TechStore.Dto;

namespace TechStore.Api.Tests;

public class OrdersApiTests : ApiTestBase
{
    public OrdersApiTests(ApiFactory factory) : base(factory)
    {
    }

    /// <summary>A customer, a warehouse, and a laptop product with 10 units received.</summary>
    private async Task<(long Customer, long Warehouse, long Laptop)> SetUpAsync(int units = 10)
    {
        var customer = await SeedCustomerAsync();
        var warehouse = await SeedWarehouseAsync();
        var laptop = await CreateProductAsync("LAP-1", price: 1000m);
        if (units > 0)
        {
            await ReceiveAsync(laptop, warehouse, units);
        }

        return (customer, warehouse, laptop);
    }

    // ---- POST /api/orders --------------------------------------------------------------------------------

    [Fact]
    public async Task PostOrder_Valid_Returns201WithLocationHeaderAndDraftOrder()
    {
        var (customer, warehouse, laptop) = await SetUpAsync();

        var response = await PostAsync("/api/orders", new
        {
            customerId = customer,
            warehouseId = warehouse,
            items = new[] { new { productId = laptop, quantity = 2 } }
        });

        var order = await ExpectAsync<OrderResponse>(response, HttpStatusCode.Created);
        Assert.Equal($"/api/orders/{order.OrderId}", response.Headers.Location?.AbsolutePath);
        Assert.Equal("Draft", order.Status);
        Assert.Equal(2, order.TotalUnits);
        Assert.Equal(2000m, order.TotalAmount);
        var item = Assert.Single(order.Items);
        Assert.Equal((laptop, "LAP-1", 2, 1000m), (item.ProductId, item.Sku, item.Quantity, item.UnitPrice));
    }

    [Fact]
    public async Task PostOrder_DoesNotReserveStock()
    {
        var (customer, warehouse, laptop) = await SetUpAsync();

        await CreateOrderAsync(customer, warehouse, laptop, 2);

        var stock = await GetStockAsync(warehouse, laptop);
        Assert.Equal((10, 0, 10), (stock.OnHand, stock.Reserved, stock.Available));
    }

    [Fact]
    public async Task PostOrder_EmptyItemsOrRepeatedProduct_Returns400()
    {
        var (customer, warehouse, laptop) = await SetUpAsync();

        var empty = await ExpectProblemAsync(
            await PostAsync("/api/orders", new { customerId = customer, warehouseId = warehouse, items = Array.Empty<object>() }),
            HttpStatusCode.BadRequest);
        var repeated = await ExpectProblemAsync(
            await PostAsync("/api/orders", new
            {
                customerId = customer,
                warehouseId = warehouse,
                items = new[] { new { productId = laptop, quantity = 1 }, new { productId = laptop, quantity = 2 } }
            }),
            HttpStatusCode.BadRequest);

        Assert.Contains("items", empty.Errors!.Keys);
        Assert.Contains("items", repeated.Errors!.Keys);
    }

    [Fact]
    public async Task PostOrder_QuantityZero_Returns400()
    {
        var (customer, warehouse, laptop) = await SetUpAsync();

        var problem = await ExpectProblemAsync(
            await PostAsync("/api/orders", new
            {
                customerId = customer,
                warehouseId = warehouse,
                items = new[] { new { productId = laptop, quantity = 0 } }
            }),
            HttpStatusCode.BadRequest);

        Assert.Contains("quantity", problem.Errors!.Keys);
    }

    [Fact]
    public async Task PostOrder_UnknownCustomerWarehouseOrProduct_Returns404()
    {
        var (customer, warehouse, laptop) = await SetUpAsync();
        var item = new[] { new { productId = laptop, quantity = 1 } };

        await ExpectProblemAsync(await PostAsync("/api/orders", new { customerId = 999, warehouseId = warehouse, items = item }), HttpStatusCode.NotFound);
        await ExpectProblemAsync(await PostAsync("/api/orders", new { customerId = customer, warehouseId = 999, items = item }), HttpStatusCode.NotFound);
        await ExpectProblemAsync(
            await PostAsync("/api/orders", new { customerId = customer, warehouseId = warehouse, items = new[] { new { productId = 999, quantity = 1 } } }),
            HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PostOrder_InactiveProduct_Returns409()
    {
        var (customer, warehouse, laptop) = await SetUpAsync();
        await PatchAsync($"/api/products/{laptop}", new { isActive = false });

        var problem = await ExpectProblemAsync(
            await PostAsync("/api/orders", new
            {
                customerId = customer,
                warehouseId = warehouse,
                items = new[] { new { productId = laptop, quantity = 1 } }
            }),
            HttpStatusCode.Conflict);

        Assert.Contains("LAP-1", problem.Detail);
    }

    // ---- GET /api/orders/{id} ----------------------------------------------------------------------------

    [Fact]
    public async Task GetOrder_ReturnsOrderWithCurrentStatus()
    {
        var (customer, warehouse, laptop) = await SetUpAsync();
        var created = await CreateOrderAsync(customer, warehouse, laptop, 2);
        await PostAsync($"/api/orders/{created.OrderId}/submit");

        var order = await ExpectAsync<OrderResponse>(await Client.GetAsync($"/api/orders/{created.OrderId}"), HttpStatusCode.OK);

        Assert.Equal(created.OrderId, order.OrderId);
        Assert.Equal("Reserved", order.Status);
    }

    [Fact]
    public async Task GetOrder_UnknownId_Returns404()
    {
        await ExpectProblemAsync(await Client.GetAsync("/api/orders/999"), HttpStatusCode.NotFound);
    }

    // ---- Status-changing actions -------------------------------------------------------------------------

    [Fact]
    public async Task Submit_ReservesStock_AndReturnsReservedOrder()
    {
        var (customer, warehouse, laptop) = await SetUpAsync();
        var order = await CreateOrderAsync(customer, warehouse, laptop, 2);

        var submitted = await ExpectAsync<OrderResponse>(await PostAsync($"/api/orders/{order.OrderId}/submit"), HttpStatusCode.OK);

        Assert.Equal("Reserved", submitted.Status);
        var stock = await GetStockAsync(warehouse, laptop);
        Assert.Equal((10, 2, 8), (stock.OnHand, stock.Reserved, stock.Available));
    }

    [Fact]
    public async Task Submit_InsufficientStock_Returns409NamingTheShortage_AndChangesNothing()
    {
        var (customer, warehouse, laptop) = await SetUpAsync(units: 10);
        var order = await CreateOrderAsync(customer, warehouse, laptop, 15);

        var problem = await ExpectProblemAsync(await PostAsync($"/api/orders/{order.OrderId}/submit"), HttpStatusCode.Conflict);

        Assert.Contains("LAP-1", problem.Detail);
        Assert.Contains("requested 15", problem.Detail);
        Assert.Contains("available 10", problem.Detail);
        var stock = await GetStockAsync(warehouse, laptop);
        Assert.Equal((10, 0), (stock.OnHand, stock.Reserved));
        var reloaded = await ExpectAsync<OrderResponse>(await Client.GetAsync($"/api/orders/{order.OrderId}"), HttpStatusCode.OK);
        Assert.Equal("Draft", reloaded.Status);
    }

    [Fact]
    public async Task Submit_Twice_SecondCallReturns409()
    {
        var (customer, warehouse, laptop) = await SetUpAsync();
        var order = await CreateOrderAsync(customer, warehouse, laptop, 2);
        await PostAsync($"/api/orders/{order.OrderId}/submit");

        await ExpectProblemAsync(await PostAsync($"/api/orders/{order.OrderId}/submit"), HttpStatusCode.Conflict);

        Assert.Equal(2, (await GetStockAsync(warehouse, laptop)).Reserved);
    }

    [Theory]
    [InlineData("submit")]
    [InlineData("pick")]
    [InlineData("pack")]
    [InlineData("ship")]
    [InlineData("cancel")]
    public async Task EveryAction_UnknownOrder_Returns404(string action)
    {
        await ExpectProblemAsync(await PostAsync($"/api/orders/999/{action}"), HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("pick")]
    [InlineData("pack")]
    [InlineData("ship")]
    public async Task FulfillmentAction_OnDraftOrder_Returns409(string action)
    {
        var (customer, warehouse, laptop) = await SetUpAsync();
        var order = await CreateOrderAsync(customer, warehouse, laptop, 2);

        await ExpectProblemAsync(await PostAsync($"/api/orders/{order.OrderId}/{action}"), HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task PickPackShip_WalkTheOrderThroughEachStatus_AndShipDeductsStock()
    {
        var (customer, warehouse, laptop) = await SetUpAsync();
        var order = await CreateOrderAsync(customer, warehouse, laptop, 2);
        await PostAsync($"/api/orders/{order.OrderId}/submit");

        var picking = await ExpectAsync<OrderResponse>(await PostAsync($"/api/orders/{order.OrderId}/pick"), HttpStatusCode.OK);
        var packed = await ExpectAsync<OrderResponse>(await PostAsync($"/api/orders/{order.OrderId}/pack"), HttpStatusCode.OK);
        var stockBeforeShip = await GetStockAsync(warehouse, laptop);
        var shipped = await ExpectAsync<OrderResponse>(await PostAsync($"/api/orders/{order.OrderId}/ship"), HttpStatusCode.OK);
        var stockAfterShip = await GetStockAsync(warehouse, laptop);

        Assert.Equal(new[] { "Picking", "Packed", "Shipped" }, new[] { picking.Status, packed.Status, shipped.Status });
        Assert.Equal((10, 2), (stockBeforeShip.OnHand, stockBeforeShip.Reserved));
        Assert.Equal((8, 0, 8), (stockAfterShip.OnHand, stockAfterShip.Reserved, stockAfterShip.Available));
    }

    [Fact]
    public async Task Ship_Twice_SecondCallReturns409_AndStockIsDeductedOnce()
    {
        var (customer, warehouse, laptop) = await SetUpAsync();
        var order = await CreateOrderAsync(customer, warehouse, laptop, 2);
        foreach (var action in new[] { "submit", "pick", "pack", "ship" })
        {
            Assert.Equal(HttpStatusCode.OK, (await PostAsync($"/api/orders/{order.OrderId}/{action}")).StatusCode);
        }

        await ExpectProblemAsync(await PostAsync($"/api/orders/{order.OrderId}/ship"), HttpStatusCode.Conflict);

        Assert.Equal(8, (await GetStockAsync(warehouse, laptop)).OnHand);
    }

    [Fact]
    public async Task Cancel_ReservedOrder_ReleasesStock()
    {
        var (customer, warehouse, laptop) = await SetUpAsync();
        var order = await CreateOrderAsync(customer, warehouse, laptop, 2);
        await PostAsync($"/api/orders/{order.OrderId}/submit");

        var canceled = await ExpectAsync<OrderResponse>(await PostAsync($"/api/orders/{order.OrderId}/cancel"), HttpStatusCode.OK);

        Assert.Equal("Canceled", canceled.Status);
        var stock = await GetStockAsync(warehouse, laptop);
        Assert.Equal((10, 0, 10), (stock.OnHand, stock.Reserved, stock.Available));
    }

    [Fact]
    public async Task Cancel_DraftOrder_Returns200_AndShippedOrder_Returns409()
    {
        var (customer, warehouse, laptop) = await SetUpAsync();
        var draft = await CreateOrderAsync(customer, warehouse, laptop, 1);
        var shipped = await CreateOrderAsync(customer, warehouse, laptop, 1);
        foreach (var action in new[] { "submit", "pick", "pack", "ship" })
        {
            await PostAsync($"/api/orders/{shipped.OrderId}/{action}");
        }

        var canceled = await ExpectAsync<OrderResponse>(await PostAsync($"/api/orders/{draft.OrderId}/cancel"), HttpStatusCode.OK);
        await ExpectProblemAsync(await PostAsync($"/api/orders/{shipped.OrderId}/cancel"), HttpStatusCode.Conflict);

        Assert.Equal("Canceled", canceled.Status);
    }

    [Fact]
    public async Task Cancel_PickingOrder_Returns409()
    {
        var (customer, warehouse, laptop) = await SetUpAsync();
        var order = await CreateOrderAsync(customer, warehouse, laptop, 2);
        await PostAsync($"/api/orders/{order.OrderId}/submit");
        await PostAsync($"/api/orders/{order.OrderId}/pick");

        await ExpectProblemAsync(await PostAsync($"/api/orders/{order.OrderId}/cancel"), HttpStatusCode.Conflict);

        Assert.Equal(2, (await GetStockAsync(warehouse, laptop)).Reserved);
    }

    // ---- GET /api/orders ---------------------------------------------------------------------------------

    [Fact]
    public async Task ListOrders_ReturnsEveryOrderNewestFirst_AsSummariesWithoutLines()
    {
        var (customer, warehouse, laptop) = await SetUpAsync();
        var first = await CreateOrderAsync(customer, warehouse, laptop, 1);
        var second = await CreateOrderAsync(customer, warehouse, laptop, 3);

        var response = await Client.GetAsync("/api/orders");

        var page = await ExpectAsync<PagedResponse<OrderListItem>>(response, HttpStatusCode.OK);
        Assert.Equal(new[] { second.OrderId, first.OrderId }, page.Items.Select(o => o.OrderId));
        Assert.Equal((1, 50, 2), (page.Page, page.PageSize, page.TotalCount));
        var newest = page.Items[0];
        Assert.Equal((customer, warehouse, "Draft", 3, 3000m), (newest.CustomerId, newest.WarehouseId, newest.Status, newest.TotalUnits, newest.TotalAmount));

        var raw = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"orderId\"", raw);
        Assert.Contains("\"totalUnits\"", raw);
        Assert.DoesNotContain("\"unitPrice\"", raw); // the list rows carry no order lines
        Assert.DoesNotContain("\"sku\"", raw);
    }

    [Fact]
    public async Task ListOrders_NoOrders_Returns200WithEmptyPage()
    {
        var page = await ExpectAsync<PagedResponse<OrderListItem>>(await Client.GetAsync("/api/orders"), HttpStatusCode.OK);

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
    }

    [Fact]
    public async Task ListOrders_RepeatedStatusParameters_AreAlternatives_AndIgnoreCase()
    {
        var (customer, warehouse, laptop) = await SetUpAsync();
        var draft = await CreateOrderAsync(customer, warehouse, laptop, 1);
        var reserved = await CreateOrderAsync(customer, warehouse, laptop, 1);
        var picking = await CreateOrderAsync(customer, warehouse, laptop, 1);
        var canceled = await CreateOrderAsync(customer, warehouse, laptop, 1);
        await PostAsync($"/api/orders/{reserved.OrderId}/submit");
        await PostAsync($"/api/orders/{picking.OrderId}/submit");
        await PostAsync($"/api/orders/{picking.OrderId}/pick");
        await PostAsync($"/api/orders/{canceled.OrderId}/cancel");

        var queue = await ExpectAsync<PagedResponse<OrderListItem>>(
            await Client.GetAsync("/api/orders?status=Reserved&status=picking&status=Packed"), HttpStatusCode.OK);
        var drafts = await ExpectAsync<PagedResponse<OrderListItem>>(await Client.GetAsync("/api/orders?status=DRAFT"), HttpStatusCode.OK);

        Assert.Equal(new[] { picking.OrderId, reserved.OrderId }, queue.Items.Select(o => o.OrderId));
        Assert.Equal(new[] { "Picking", "Reserved" }, queue.Items.Select(o => o.Status));
        Assert.Equal(new[] { draft.OrderId }, drafts.Items.Select(o => o.OrderId));
    }

    [Fact]
    public async Task ListOrders_CustomerAndWarehouseFilters_NarrowTheList()
    {
        var ann = await SeedCustomerAsync("Ann");
        var bob = await SeedCustomerAsync("Bob");
        var dallas = await SeedWarehouseAsync("Dallas");
        var seattle = await SeedWarehouseAsync("Seattle");
        var laptop = await CreateProductAsync();
        await CreateOrderAsync(ann, dallas, laptop, 1);
        var annSeattle = await CreateOrderAsync(ann, seattle, laptop, 1);
        await CreateOrderAsync(bob, seattle, laptop, 1);

        var both = await ExpectAsync<PagedResponse<OrderListItem>>(
            await Client.GetAsync($"/api/orders?customerId={ann}&warehouseId={seattle}"), HttpStatusCode.OK);

        Assert.Equal(new[] { annSeattle.OrderId }, both.Items.Select(o => o.OrderId));
    }

    [Fact]
    public async Task ListOrders_PagingParametersAreHonored()
    {
        var (customer, warehouse, laptop) = await SetUpAsync();
        for (var i = 0; i < 3; i++)
        {
            await CreateOrderAsync(customer, warehouse, laptop, 1);
        }

        var page2 = await ExpectAsync<PagedResponse<OrderListItem>>(await Client.GetAsync("/api/orders?page=2&pageSize=2"), HttpStatusCode.OK);

        Assert.Equal(new long[] { 1 }, page2.Items.Select(o => o.OrderId));
        Assert.Equal((2, 2, 3), (page2.Page, page2.PageSize, page2.TotalCount));
    }

    [Theory]
    [InlineData("/api/orders?status=Bogus")]
    [InlineData("/api/orders?status=Reserved&status=1")]
    [InlineData("/api/orders?pageSize=201")]
    [InlineData("/api/orders?page=0")]
    [InlineData("/api/orders?customerId=0")]
    [InlineData("/api/orders?warehouseId=-4")]
    public async Task ListOrders_InvalidFilters_Return400Problem(string url)
    {
        await ExpectProblemAsync(await Client.GetAsync(url), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ListOrders_InvalidStatus_NamesTheFieldAndTheAllowedValues()
    {
        var problem = await ExpectProblemAsync(await Client.GetAsync("/api/orders?status=Bogus"), HttpStatusCode.BadRequest);

        Assert.Contains("status", problem.Errors!.Keys);
        Assert.Contains("Reserved", problem.Errors["status"][0]);
    }

    [Fact]
    public async Task ListOrders_NonNumericCustomerId_Returns400Problem()
    {
        await ExpectProblemAsync(await Client.GetAsync("/api/orders?customerId=abc"), HttpStatusCode.BadRequest);
    }
}
