using System.Net;
using TechStore.Api.Tests.Infrastructure;
using TechStore.Dto;

namespace TechStore.Api.Tests;

public class ReferenceDataApiTests : ApiTestBase
{
    public ReferenceDataApiTests(ApiFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task GetWarehouses_ReturnsListOrderedByName()
    {
        await SeedWarehouseAsync("Seattle");
        await SeedWarehouseAsync("Dallas");

        var warehouses = await ExpectAsync<List<WarehouseResponse>>(await Client.GetAsync("/api/warehouses"), HttpStatusCode.OK);

        Assert.Equal(new[] { "Dallas", "Seattle" }, warehouses.Select(w => w.Name));
        Assert.Equal("Dallas, USA", warehouses[0].Location);
    }

    [Fact]
    public async Task GetWarehouses_NoneExist_ReturnsEmptyArray()
    {
        var response = await Client.GetAsync("/api/warehouses");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("[]", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task GetCustomers_ReturnsListOrderedByName()
    {
        await SeedCustomerAsync("Zed Corp");
        await SeedCustomerAsync("Ann Lee");

        var customers = await ExpectAsync<List<CustomerResponse>>(await Client.GetAsync("/api/customers"), HttpStatusCode.OK);

        Assert.Equal(new[] { "Ann Lee", "Zed Corp" }, customers.Select(c => c.Name));
        Assert.Equal("ann@example.com", customers[0].Email);
    }
}
