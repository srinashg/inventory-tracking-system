using System.Net;
using TechStore.Api.Tests.Infrastructure;
using TechStore.Dto;

namespace TechStore.Api.Tests;

public class InventoryApiTests : ApiTestBase
{
    public InventoryApiTests(ApiFactory factory) : base(factory)
    {
    }

    // ---- GET /api/inventory ------------------------------------------------------------------------------

    [Fact]
    public async Task GetInventory_ReturnsOnHandReservedAndAvailable()
    {
        var warehouse = await SeedWarehouseAsync();
        var customer = await SeedCustomerAsync();
        var laptop = await CreateProductAsync("LAP-1");
        await ReceiveAsync(laptop, warehouse, 10);
        var order = await CreateOrderAsync(customer, warehouse, laptop, 2);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync($"/api/orders/{order.OrderId}/submit")).StatusCode);

        var page = await ExpectAsync<PagedResponse<InventoryItemResponse>>(
            await Client.GetAsync($"/api/inventory?warehouseId={warehouse}"), HttpStatusCode.OK);

        var row = Assert.Single(page.Items);
        Assert.Equal((laptop, "LAP-1", 10, 2, 8), (row.ProductId, row.Sku, row.OnHand, row.Reserved, row.Available));
    }

    [Theory]
    [InlineData("/api/inventory")]
    [InlineData("/api/inventory?warehouseId=0")]
    [InlineData("/api/inventory?warehouseId=1&pageSize=500")]
    public async Task GetInventory_MissingOrInvalidParameters_Returns400(string url)
    {
        await SeedWarehouseAsync();

        await ExpectProblemAsync(await Client.GetAsync(url), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetInventory_UnknownWarehouse_Returns404()
    {
        await ExpectProblemAsync(await Client.GetAsync("/api/inventory?warehouseId=999"), HttpStatusCode.NotFound);
    }

    // ---- POST /api/inventory/receipts --------------------------------------------------------------------

    [Fact]
    public async Task PostReceipt_Valid_Returns201WithUpdatedBalance()
    {
        var warehouse = await SeedWarehouseAsync();
        var laptop = await CreateProductAsync();

        var response = await PostAsync("/api/inventory/receipts",
            new { productId = laptop, warehouseId = warehouse, quantity = 10, note = "Supplier delivery" });

        var change = await ExpectAsync<StockChangeResponse>(response, HttpStatusCode.Created);
        Assert.True(change.MovementId > 0);
        Assert.Equal((laptop, warehouse, 10, 0, 10),
            (change.ProductId, change.WarehouseId, change.OnHand, change.Reserved, change.Available));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(1_000_001)]
    public async Task PostReceipt_QuantityOutOfRange_Returns400(int quantity)
    {
        var warehouse = await SeedWarehouseAsync();
        var laptop = await CreateProductAsync();

        var problem = await ExpectProblemAsync(
            await PostAsync("/api/inventory/receipts", new { productId = laptop, warehouseId = warehouse, quantity }),
            HttpStatusCode.BadRequest);

        Assert.Contains("quantity", problem.Errors!.Keys);

        // Nothing was received.
        var stock = await ExpectAsync<PagedResponse<InventoryItemResponse>>(
            await Client.GetAsync($"/api/inventory?warehouseId={warehouse}"), HttpStatusCode.OK);
        Assert.Empty(stock.Items);
    }

    [Fact]
    public async Task PostReceipt_UnknownProductOrWarehouse_Returns404()
    {
        var warehouse = await SeedWarehouseAsync();
        var laptop = await CreateProductAsync();

        await ExpectProblemAsync(
            await PostAsync("/api/inventory/receipts", new { productId = 999, warehouseId = warehouse, quantity = 1 }),
            HttpStatusCode.NotFound);
        await ExpectProblemAsync(
            await PostAsync("/api/inventory/receipts", new { productId = laptop, warehouseId = 999, quantity = 1 }),
            HttpStatusCode.NotFound);
    }

    // ---- POST /api/inventory/adjustments -----------------------------------------------------------------

    [Fact]
    public async Task PostAdjustment_Valid_Returns201WithUpdatedBalance()
    {
        var warehouse = await SeedWarehouseAsync();
        var laptop = await CreateProductAsync();
        await ReceiveAsync(laptop, warehouse, 10);

        var response = await PostAsync("/api/inventory/adjustments",
            new { productId = laptop, warehouseId = warehouse, quantityChange = -2, reason = "Damaged" });

        var change = await ExpectAsync<StockChangeResponse>(response, HttpStatusCode.Created);
        Assert.Equal((8, 0, 8), (change.OnHand, change.Reserved, change.Available));
    }

    [Fact]
    public async Task PostAdjustment_BlankReason_Returns400()
    {
        var warehouse = await SeedWarehouseAsync();
        var laptop = await CreateProductAsync();
        await ReceiveAsync(laptop, warehouse, 10);

        var problem = await ExpectProblemAsync(
            await PostAsync("/api/inventory/adjustments",
                new { productId = laptop, warehouseId = warehouse, quantityChange = 1, reason = "  " }),
            HttpStatusCode.BadRequest);

        Assert.Contains("reason", problem.Errors!.Keys);
    }

    [Fact]
    public async Task PostAdjustment_BelowReservedStock_Returns409_AndStockIsUnchanged()
    {
        var warehouse = await SeedWarehouseAsync();
        var customer = await SeedCustomerAsync();
        var laptop = await CreateProductAsync();
        await ReceiveAsync(laptop, warehouse, 10);
        var order = await CreateOrderAsync(customer, warehouse, laptop, 4);
        await PostAsync($"/api/orders/{order.OrderId}/submit");

        await ExpectProblemAsync(
            await PostAsync("/api/inventory/adjustments",
                new { productId = laptop, warehouseId = warehouse, quantityChange = -7, reason = "Recount" }),
            HttpStatusCode.Conflict);

        var stock = await GetStockAsync(warehouse, laptop);
        Assert.Equal((10, 4), (stock.OnHand, stock.Reserved));
    }

    [Fact]
    public async Task PostAdjustment_NoBalanceYet_Returns404()
    {
        var warehouse = await SeedWarehouseAsync();
        var laptop = await CreateProductAsync();

        await ExpectProblemAsync(
            await PostAsync("/api/inventory/adjustments",
                new { productId = laptop, warehouseId = warehouse, quantityChange = 1, reason = "Recount" }),
            HttpStatusCode.NotFound);
    }
}
