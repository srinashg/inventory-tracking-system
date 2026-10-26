using System.Net;
using System.Net.Http.Json;
using TechStore.Api.Tests.Infrastructure;
using TechStore.Dto;

namespace TechStore.Api.Tests;

/// <summary>The dashboard endpoint, the health check, and cross-cutting error behavior.</summary>
public class DashboardAndPlatformApiTests : ApiTestBase
{
    public DashboardAndPlatformApiTests(ApiFactory factory) : base(factory)
    {
    }

    // ---- GET /api/dashboard/summary ----------------------------------------------------------------------

    [Fact]
    public async Task GetDashboardSummary_ReturnsTotalsMatchingTheData()
    {
        var customer = await SeedCustomerAsync();
        var warehouse = await SeedWarehouseAsync();
        var laptop = await CreateProductAsync("LAP-1");
        await ReceiveAsync(laptop, warehouse, 8);
        var shipped = await CreateOrderAsync(customer, warehouse, laptop, 3);
        foreach (var action in new[] { "submit", "pick", "pack", "ship" })
        {
            await PostAsync($"/api/orders/{shipped.OrderId}/{action}");
        }

        var open = await CreateOrderAsync(customer, warehouse, laptop, 1); // stays Draft
        Assert.NotNull(open);

        var summary = await ExpectAsync<DashboardSummaryResponse>(await Client.GetAsync("/api/dashboard/summary"), HttpStatusCode.OK);

        Assert.Equal(5, summary.TotalAvailableUnits);   // 8 received - 3 shipped
        Assert.Equal(5, summary.LowStockThreshold);
        Assert.Equal(1, summary.LowStockCount);          // 5 available is at the threshold
        Assert.Equal("LAP-1", Assert.Single(summary.LowStockItems).Sku);
        Assert.Equal((1, 1), (summary.OpenOrders, summary.ShippedOrders));
    }

    [Fact]
    public async Task GetDashboardSummary_EmptySystem_ReturnsZeros()
    {
        var summary = await ExpectAsync<DashboardSummaryResponse>(await Client.GetAsync("/api/dashboard/summary"), HttpStatusCode.OK);

        Assert.Equal((0L, 0, 0, 0), (summary.TotalAvailableUnits, summary.LowStockCount, summary.OpenOrders, summary.ShippedOrders));
        Assert.Empty(summary.LowStockItems);
    }

    // ---- GET /health -------------------------------------------------------------------------------------

    [Fact]
    public async Task Health_WithDatabaseReachable_Returns200Healthy()
    {
        var response = await Client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Health_WithDatabaseUnreachable_Returns503Unhealthy()
    {
        await using var broken = new BrokenDatabaseFactory();
        using var client = broken.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("Unhealthy", await response.Content.ReadAsStringAsync());
    }

    // ---- Error handling ----------------------------------------------------------------------------------

    [Fact]
    public async Task UnexpectedFailure_Returns500WithGenericMessage_AndNoInternalDetails()
    {
        await using var broken = new BrokenDatabaseFactory();
        using var client = broken.Browser();

        // Signing in is an anonymous request that has to read the database, which is unreachable here.
        var response = await client.PostAsJsonAsync("/api/auth/login", new { email = "someone@test.local", password = "pw-not-important-123" });

        var problem = await ExpectProblemAsync(response, HttpStatusCode.InternalServerError);
        Assert.Equal("An unexpected error occurred.", problem.Detail);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Npgsql", raw);
        Assert.DoesNotContain("localhost", raw);
        Assert.DoesNotContain("   at ", raw); // no stack trace
        Assert.DoesNotContain("pw-not-important-123", raw);
    }

    [Fact]
    public async Task UnknownRoute_Returns404()
    {
        var response = await Client.GetAsync("/api/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task WrongHttpMethod_Returns405()
    {
        var response = await Client.DeleteAsync("/api/products");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }
}
