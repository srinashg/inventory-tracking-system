using System.Net;
using TechStore.Api.Tests.Infrastructure;
using TechStore.Dto;

namespace TechStore.Api.Tests;

/// <summary>The blueprint's final acceptance scenario, driven entirely through HTTP.</summary>
public class OrderJourneyApiTests : ApiTestBase
{
    public OrderJourneyApiTests(ApiFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task FinalAcceptanceScenario_Receive10_Order2_ReservePickPackShip_Leaves8Available_ThenOrder15IsRejected()
    {
        var customer = await SeedCustomerAsync();
        var warehouse = await SeedWarehouseAsync("Dallas");
        var laptop = await CreateProductAsync("LAP-1", price: 1299.99m, name: "Laptop");

        // Receive 10 laptops.
        await ReceiveAsync(laptop, warehouse, 10);
        var stock = await GetStockAsync(warehouse, laptop);
        Assert.Equal((10, 0, 10), (stock.OnHand, stock.Reserved, stock.Available));

        // Draft an order for 2, then submit it: 2 reserved, 8 available.
        var order = await CreateOrderAsync(customer, warehouse, laptop, 2);
        Assert.Equal("Draft", order.Status);
        await ExpectAsync<OrderResponse>(await PostAsync($"/api/orders/{order.OrderId}/submit"), HttpStatusCode.OK);
        stock = await GetStockAsync(warehouse, laptop);
        Assert.Equal((10, 2, 8), (stock.OnHand, stock.Reserved, stock.Available));

        // Pick, pack, ship: 8 on hand, nothing reserved, 8 available.
        await ExpectAsync<OrderResponse>(await PostAsync($"/api/orders/{order.OrderId}/pick"), HttpStatusCode.OK);
        await ExpectAsync<OrderResponse>(await PostAsync($"/api/orders/{order.OrderId}/pack"), HttpStatusCode.OK);
        var shipped = await ExpectAsync<OrderResponse>(await PostAsync($"/api/orders/{order.OrderId}/ship"), HttpStatusCode.OK);
        Assert.Equal("Shipped", shipped.Status);
        stock = await GetStockAsync(warehouse, laptop);
        Assert.Equal((8, 0, 8), (stock.OnHand, stock.Reserved, stock.Available));

        // Shipping again is refused and does not deduct twice.
        await ExpectProblemAsync(await PostAsync($"/api/orders/{order.OrderId}/ship"), HttpStatusCode.Conflict);
        Assert.Equal(8, (await GetStockAsync(warehouse, laptop)).OnHand);

        // An order for 15 with only 8 available is refused, and stock is not corrupted.
        var tooBig = await CreateOrderAsync(customer, warehouse, laptop, 15);
        var problem = await ExpectProblemAsync(await PostAsync($"/api/orders/{tooBig.OrderId}/submit"), HttpStatusCode.Conflict);
        Assert.Contains("Insufficient stock", problem.Detail);
        stock = await GetStockAsync(warehouse, laptop);
        Assert.Equal((8, 0, 8), (stock.OnHand, stock.Reserved, stock.Available));
    }

    [Fact]
    public async Task CancelingAReservedOrder_RestoresAvailability()
    {
        var customer = await SeedCustomerAsync();
        var warehouse = await SeedWarehouseAsync();
        var laptop = await CreateProductAsync();
        await ReceiveAsync(laptop, warehouse, 10);
        var order = await CreateOrderAsync(customer, warehouse, laptop, 4);
        await PostAsync($"/api/orders/{order.OrderId}/submit");
        Assert.Equal(6, (await GetStockAsync(warehouse, laptop)).Available);

        await ExpectAsync<OrderResponse>(await PostAsync($"/api/orders/{order.OrderId}/cancel"), HttpStatusCode.OK);

        Assert.Equal(10, (await GetStockAsync(warehouse, laptop)).Available);
    }

    [Fact]
    public async Task ConcurrentSubmitsOverHttp_NeverOversell()
    {
        var customer = await SeedCustomerAsync();
        var warehouse = await SeedWarehouseAsync();
        var laptop = await CreateProductAsync();
        await ReceiveAsync(laptop, warehouse, 10);
        var orders = new List<OrderResponse>();
        for (var i = 0; i < 25; i++)
        {
            orders.Add(await CreateOrderAsync(customer, warehouse, laptop, 1));
        }

        var statuses = await Task.WhenAll(orders.Select(async o =>
            (await PostAsync($"/api/orders/{o.OrderId}/submit")).StatusCode));

        Assert.Equal(10, statuses.Count(s => s == HttpStatusCode.OK));
        Assert.Equal(15, statuses.Count(s => s == HttpStatusCode.Conflict));
        var stock = await GetStockAsync(warehouse, laptop);
        Assert.Equal((10, 10, 0), (stock.OnHand, stock.Reserved, stock.Available));
    }
}
