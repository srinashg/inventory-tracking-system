using TechStore.Services.Tests.Infrastructure;

namespace TechStore.Services.Tests;

public class ReferenceDataServiceTests : DatabaseTestBase
{
    public ReferenceDataServiceTests(DatabaseFixture fixture) : base(fixture)
    {
    }

    private async Task<T> RunAsync<T>(Func<ReferenceDataService, Task<T>> action)
    {
        await using var db = NewContext();
        return await action(new ReferenceDataService(db));
    }

    [Fact]
    public async Task ListWarehouses_ReturnsAllOrderedByName()
    {
        await SeedWarehouseAsync("Seattle", "Seattle, WA");
        await SeedWarehouseAsync("Dallas", "Dallas, TX");

        var result = await RunAsync(s => s.ListWarehousesAsync());

        Assert.Equal(new[] { "Dallas", "Seattle" }, result.Select(w => w.Name));
        Assert.Equal("Dallas, TX", result[0].Location);
        Assert.True(result[0].WarehouseId > 0);
    }

    [Fact]
    public async Task ListWarehouses_NoRows_ReturnsEmptyList()
    {
        var result = await RunAsync(s => s.ListWarehousesAsync());

        Assert.Empty(result);
    }

    [Fact]
    public async Task ListCustomers_ReturnsAllOrderedByName()
    {
        await SeedCustomerAsync("Zed Corp", "zed@example.com");
        await SeedCustomerAsync("Ann Lee", "ann@example.com");

        var result = await RunAsync(s => s.ListCustomersAsync());

        Assert.Equal(new[] { "Ann Lee", "Zed Corp" }, result.Select(c => c.Name));
        Assert.Equal("ann@example.com", result[0].Email);
        Assert.True(result[0].CustomerId > 0);
    }

    [Fact]
    public async Task ListCustomers_NoRows_ReturnsEmptyList()
    {
        var result = await RunAsync(s => s.ListCustomersAsync());

        Assert.Empty(result);
    }
}
