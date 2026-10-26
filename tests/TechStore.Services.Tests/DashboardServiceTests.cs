using TechStore.Database.Entities;
using TechStore.Dto;
using TechStore.Services.Tests.Infrastructure;

namespace TechStore.Services.Tests;

public class DashboardServiceTests : DatabaseTestBase
{
    public DashboardServiceTests(DatabaseFixture fixture) : base(fixture)
    {
    }

    private async Task<DashboardSummaryResponse> GetSummaryAsync()
    {
        await using var db = NewContext();
        return await new DashboardService(db).GetSummaryAsync();
    }

    [Fact]
    public async Task GetSummary_EmptyDatabase_ReturnsZerosAndEmptyList()
    {
        var summary = await GetSummaryAsync();

        Assert.Equal(0, summary.TotalAvailableUnits);
        Assert.Equal(0, summary.LowStockCount);
        Assert.Empty(summary.LowStockItems);
        Assert.Equal(0, summary.OpenOrders);
        Assert.Equal(0, summary.ShippedOrders);
        Assert.Equal(5, summary.LowStockThreshold);
    }

    [Fact]
    public async Task GetSummary_TotalAvailableUnits_SumsOnHandMinusReservedAcrossWarehouses()
    {
        var dallas = await SeedWarehouseAsync("Dallas");
        var seattle = await SeedWarehouseAsync("Seattle");
        var laptop = await SeedProductAsync();
        var mouse = await SeedProductAsync();
        await SeedBalanceAsync(laptop, dallas, onHand: 10, reserved: 2);   // 8 available
        await SeedBalanceAsync(laptop, seattle, onHand: 20);               // 20
        await SeedBalanceAsync(mouse, dallas, onHand: 7, reserved: 7);     // 0

        var summary = await GetSummaryAsync();

        Assert.Equal(28, summary.TotalAvailableUnits);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(5, true)]
    [InlineData(6, false)]
    public async Task GetSummary_LowStockThreshold_IsInclusiveAtFive(int available, bool isLow)
    {
        var warehouse = await SeedWarehouseAsync();
        var product = await SeedProductAsync("LOW-1", name: "Cable");
        await SeedBalanceAsync(product, warehouse, onHand: available + 3, reserved: 3);

        var summary = await GetSummaryAsync();

        Assert.Equal(isLow ? 1 : 0, summary.LowStockCount);
        if (isLow)
        {
            var item = Assert.Single(summary.LowStockItems);
            Assert.Equal((product, "LOW-1", "Cable", warehouse, available),
                (item.ProductId, item.Sku, item.Name, item.WarehouseId, item.Available));
        }
    }

    [Fact]
    public async Task GetSummary_LowStock_ExcludesInactiveProducts_AndIsOrderedLowestFirst()
    {
        var warehouse = await SeedWarehouseAsync();
        var three = await SeedProductAsync("B-3");
        var one = await SeedProductAsync("A-1");
        var inactive = await SeedProductAsync("OLD", isActive: false);
        await SeedBalanceAsync(three, warehouse, onHand: 3);
        await SeedBalanceAsync(one, warehouse, onHand: 1);
        await SeedBalanceAsync(inactive, warehouse, onHand: 0);

        var summary = await GetSummaryAsync();

        Assert.Equal(2, summary.LowStockCount);
        Assert.Equal(new[] { "A-1", "B-3" }, summary.LowStockItems.Select(i => i.Sku));
    }

    [Fact]
    public async Task GetSummary_MoreThan50LowStockItems_ReturnsFiftyButCountsAll()
    {
        var warehouse = await SeedWarehouseAsync();
        for (var i = 0; i < 55; i++)
        {
            var product = await SeedProductAsync();
            await SeedBalanceAsync(product, warehouse, onHand: 1);
        }

        var summary = await GetSummaryAsync();

        Assert.Equal(55, summary.LowStockCount);
        Assert.Equal(50, summary.LowStockItems.Count);
    }

    [Fact]
    public async Task GetSummary_CountsOpenAndShippedOrders_AndIgnoresCanceled()
    {
        var customer = await SeedCustomerAsync();
        var warehouse = await SeedWarehouseAsync();
        var product = await SeedProductAsync();
        foreach (var status in new[]
        {
            OrderStatus.Draft, OrderStatus.Reserved, OrderStatus.Picking, OrderStatus.Packed,
            OrderStatus.Shipped, OrderStatus.Shipped, OrderStatus.Shipped,
            OrderStatus.Canceled, OrderStatus.Canceled
        })
        {
            await SeedOrderAsync(customer, warehouse, status, (product, 1, 10m));
        }

        var summary = await GetSummaryAsync();

        Assert.Equal(4, summary.OpenOrders);    // Draft + Reserved + Picking + Packed
        Assert.Equal(3, summary.ShippedOrders);
    }

    [Fact]
    public async Task GetSummary_AfterShipping_AnOrderMovesFromOpenToShipped()
    {
        var customer = await SeedCustomerAsync();
        var warehouse = await SeedWarehouseAsync();
        var product = await SeedProductAsync();
        await SeedBalanceAsync(product, warehouse, onHand: 10, reserved: 1);
        var orderId = await SeedOrderAsync(customer, warehouse, OrderStatus.Packed, (product, 1, 10m));

        var before = await GetSummaryAsync();
        await using (var db = NewContext())
        {
            await new FulfillmentService(db, new ListLogger<FulfillmentService>()).ShipOrderAsync(orderId);
        }
        var after = await GetSummaryAsync();

        Assert.Equal((1, 0), (before.OpenOrders, before.ShippedOrders));
        Assert.Equal((0, 1), (after.OpenOrders, after.ShippedOrders));
        Assert.Equal(9, after.TotalAvailableUnits);
    }
}
