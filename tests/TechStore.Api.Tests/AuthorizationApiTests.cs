using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TechStore.Api.Tests.Infrastructure;

namespace TechStore.Api.Tests;

/// <summary>
/// The permission matrix of docs/api-contract.md section 2, checked end to end: anonymous callers get 401, every role gets success
/// where the matrix says allowed and 403 where it says forbidden.
/// </summary>
public class AuthorizationApiTests : ApiTestBase
{
    public AuthorizationApiTests(ApiFactory factory) : base(factory)
    {
    }

    private static HttpRequestMessage Request(EndpointRule rule)
    {
        var request = new HttpRequestMessage(new HttpMethod(rule.Method), rule.Path);
        if (rule.Method is "POST" or "PATCH") request.Content = JsonContent.Create(new { });
        return request;
    }

    [Fact]
    public async Task AnonymousCallers_GetA401_OnEveryEndpoint_ExceptTheOpenOnes()
    {
        var client = RawClient(); // no session and no CSRF token: authentication must be checked first
        var mismatches = new List<string>();

        foreach (var rule in PermissionMatrix.Rules.Where(r => !r.IsAnonymous))
        {
            var response = await client.SendAsync(Request(rule));
            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                mismatches.Add($"{rule}: expected 401, got {(int)response.StatusCode}");
            }
            else if (response.Content.Headers.ContentType?.MediaType != "application/problem+json" || response.Headers.Location is not null)
            {
                mismatches.Add($"{rule}: 401 was not a plain problem+json response");
            }
        }

        Assert.Empty(mismatches);
    }

    [Fact]
    public async Task TheOpenEndpoints_NeedNoSession()
    {
        var client = AnonymousClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/csrf")).StatusCode);
        // Login is reachable without a session; an empty body is a validation error (400), not "unauthorized".
        var login = await client.PostAsJsonAsync("/api/auth/login", new { }, Json);
        Assert.Equal(HttpStatusCode.BadRequest, login.StatusCode);
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Sales")]
    [InlineData("Warehouse")]
    public async Task EachRole_GetsExactlyTheDocumentedAccess(string role)
    {
        var client = await SignedInClientAsync(role);
        var mismatches = new List<string>();

        foreach (var rule in PermissionMatrix.Rules.Where(r => !r.IsAnonymous && r.Path != "/api/auth/logout"))
        {
            var response = await client.SendAsync(Request(rule));
            var status = response.StatusCode;

            if (rule.Allows(role))
            {
                if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    mismatches.Add($"{role} on {rule}: should be allowed but got {(int)status}");
                }
            }
            else if (status != HttpStatusCode.Forbidden)
            {
                mismatches.Add($"{role} on {rule}: should be forbidden (403) but got {(int)status}");
            }
            else if (response.Content.Headers.ContentType?.MediaType != "application/problem+json")
            {
                mismatches.Add($"{role} on {rule}: 403 was not problem+json");
            }
        }

        Assert.Empty(mismatches);
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Sales")]
    [InlineData("Warehouse")]
    public async Task EachRole_CanSignOut(string role)
    {
        var client = await SignedInClientAsync(role);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/logout", null)).StatusCode);
    }

    [Fact]
    public async Task ASalesUser_CannotAdjustStock_EvenWithAValidRequest()
    {
        var sales = await SignedInClientAsync("Sales");
        var productId = await CreateProductAsync();
        var warehouseId = await SeedWarehouseAsync();
        await ReceiveAsync(productId, warehouseId, 10);

        var response = await sales.PostAsJsonAsync("/api/inventory/adjustments", new { productId, warehouseId, quantityChange = -5, reason = "Because" }, Json);

        var problem = await ExpectProblemAsync(response, HttpStatusCode.Forbidden);
        Assert.Equal("Your role does not allow this action.", problem.Detail);
        Assert.Equal(10, (await GetStockAsync(warehouseId, productId)).OnHand); // nothing changed
    }

    [Fact]
    public async Task AWarehouseUser_CannotCreateOrSubmitOrders_ButCanReceiveStockAndFulfill()
    {
        var customer = await SeedCustomerAsync();
        var warehouseId = await SeedWarehouseAsync();
        var productId = await CreateProductAsync();
        var warehouse = await SignedInClientAsync("Warehouse");

        var createOrder = await warehouse.PostAsJsonAsync("/api/orders",
            new { customerId = customer, warehouseId, items = new[] { new { productId, quantity = 1 } } }, Json);
        var receive = await warehouse.PostAsJsonAsync("/api/inventory/receipts", new { productId, warehouseId, quantity = 10 }, Json);

        Assert.Equal(HttpStatusCode.Forbidden, createOrder.StatusCode);
        Assert.Equal(HttpStatusCode.Created, receive.StatusCode);

        var order = await CreateOrderAsync(customer, warehouseId, productId, 2); // created by the Admin client
        Assert.Equal(HttpStatusCode.Forbidden, (await warehouse.PostAsync($"/api/orders/{order.OrderId}/submit", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client.PostAsync($"/api/orders/{order.OrderId}/submit", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await warehouse.PostAsync($"/api/orders/{order.OrderId}/pick", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await warehouse.PostAsync($"/api/orders/{order.OrderId}/pack", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await warehouse.PostAsync($"/api/orders/{order.OrderId}/ship", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await warehouse.PostAsync($"/api/orders/{order.OrderId}/cancel", null)).StatusCode);
    }

    [Fact]
    public async Task ASalesUser_CanCreateSubmitAndCancelOrders_ButCannotFulfillThem()
    {
        var customer = await SeedCustomerAsync();
        var warehouseId = await SeedWarehouseAsync();
        var productId = await CreateProductAsync();
        await ReceiveAsync(productId, warehouseId, 10);
        var sales = await SignedInClientAsync("Sales");

        var created = await sales.PostAsJsonAsync("/api/orders",
            new { customerId = customer, warehouseId, items = new[] { new { productId, quantity = 2 } } }, Json);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var orderId = (await ReadAsync<TechStore.Dto.OrderResponse>(created)).OrderId;

        Assert.Equal(HttpStatusCode.OK, (await sales.PostAsync($"/api/orders/{orderId}/submit", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await sales.PostAsync($"/api/orders/{orderId}/pick", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await sales.PostAsync($"/api/orders/{orderId}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await sales.GetAsync($"/api/orders/{orderId}")).StatusCode); // read access
        Assert.Equal(HttpStatusCode.OK, (await sales.GetAsync($"/api/inventory?warehouseId={warehouseId}")).StatusCode);
    }

    [Fact]
    public async Task OnlyAnAdmin_CanChangeTheProductCatalog()
    {
        foreach (var role in new[] { "Sales", "Warehouse" })
        {
            var client = await SignedInClientAsync(role);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/products", new { sku = "X-1", name = "X", price = 1 }, Json)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Created, (await Client.PostAsJsonAsync("/api/products", new { sku = "X-1", name = "X", price = 1 }, Json)).StatusCode);
    }

    [Fact]
    public async Task APermissionFailure_IsReportedBeforeACsrfProblem()
    {
        // A Sales user without a CSRF token who tries something forbidden still gets 403, not 400.
        var bare = RawClient();
        var account = Factory.Account("Sales");
        var token = (await ExpectAsync<TechStore.Dto.CsrfTokenResponse>(await bare.GetAsync("/api/auth/csrf"), HttpStatusCode.OK)).Token;
        await bare.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { email = account.Email, password = account.Password }),
            Headers = { { "X-CSRF-TOKEN", token } },
        });

        var response = await bare.PostAsJsonAsync("/api/inventory/adjustments", new { }, Json);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- The matrix cannot drift away from the real endpoints -------------------------------------------

    [Fact]
    public void EveryApiEndpointThatExists_IsListedInThePermissionMatrix_AndNothingElse()
    {
        var actual = Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .SelectMany(endpoint =>
            {
                var template = endpoint.RoutePattern.RawText!.TrimStart('/');
                // The health-check endpoint has no method restriction, so it is listed as the GET it is used for.
                var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? new[] { "GET" };
                return methods.Select(method => $"{method} {template}");
            })
            .Where(key => key.Contains(" api/") || key.EndsWith(" health"))
            .Distinct()
            .OrderBy(key => key)
            .ToList();

        var documented = PermissionMatrix.Rules.Select(r => $"{r.Method} {r.Template}").OrderBy(key => key).ToList();

        Assert.Equal(documented, actual);
    }
}
