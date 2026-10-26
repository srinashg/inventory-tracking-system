using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TechStore.Auth;
using TechStore.Database.Entities;
using TechStore.Dto;

namespace TechStore.Api.Tests.Infrastructure;

/// <summary>The shape of every error body, as documented in docs/api-contract.md section 1.2.</summary>
public record ProblemBody(int Status, string Title, string? Detail, Dictionary<string, string[]>? Errors);

/// <summary>
/// Base class for API tests. Empties the database before each test and offers helpers that call the API with
/// JSON and seed the data that has no endpoint (warehouses, customers).
/// </summary>
[Collection(ApiCollection.Name)]
public abstract class ApiTestBase : IAsyncLifetime
{
    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ApiFactory _factory;
    private readonly List<HttpClient> _clients = new();

    protected ApiTestBase(ApiFactory factory)
    {
        _factory = factory;
    }

    protected ApiFactory Factory => _factory;

    /// <summary>A browser signed in as the Admin test account. Sends the CSRF token automatically on POST and PATCH.</summary>
    protected HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await using (var db = _factory.CreateDbContext())
        {
            await db.Database.ExecuteSqlRawAsync(
                "TRUNCATE TABLE inventory_movements, order_items, orders, inventory_balances, products, warehouses, customers, " +
                "user_roles, user_claims, user_logins, user_tokens, role_claims, users, roles RESTART IDENTITY CASCADE");
        }

        // Recreate the three test accounts, exactly as the API does at startup in the Testing environment.
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<AuthInitializer>().InitializeAsync();
        }

        Client = await SignedInClientAsync("Admin");
    }

    public Task DisposeAsync()
    {
        foreach (var client in _clients) client.Dispose();
        return Task.CompletedTask;
    }

    // ---- Clients (each one is a separate browser with its own cookies) ------------------------------------

    /// <summary>A browser that is not signed in. It still sends the CSRF token on POST/PATCH once it has fetched one.</summary>
    protected HttpClient AnonymousClient() => Track(_factory.Browser());

    /// <summary>A bare browser: no CSRF token is ever added for you. For testing the CSRF protection itself.</summary>
    protected HttpClient RawClient() => Track(_factory.Browser(addCsrfToken: false));

    protected async Task<HttpClient> SignedInClientAsync(string role)
    {
        var client = AnonymousClient();
        var account = _factory.Account(role);
        var response = await client.PostAsJsonAsync("/api/auth/login", new { email = account.Email, password = account.Password }, Json);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private HttpClient Track(HttpClient client)
    {
        _clients.Add(client);
        return client;
    }

    // ---- Calling the API ---------------------------------------------------------------------------------

    protected Task<HttpResponseMessage> PostAsync(string url, object? body = null) =>
        Client.PostAsJsonAsync(url, body ?? new { }, Json);

    protected Task<HttpResponseMessage> PatchAsync(string url, object body) =>
        Client.PatchAsync(url, JsonContent.Create(body, options: Json));

    protected static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(Json))!;

    /// <summary>Asserts the status code and returns the parsed body.</summary>
    protected static async Task<T> ExpectAsync<T>(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        return await ReadAsync<T>(response);
    }

    /// <summary>Asserts a documented error: status code, application/problem+json, and a readable message.</summary>
    protected static async Task<ProblemBody> ExpectProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await ReadAsync<ProblemBody>(response);
        Assert.Equal((int)status, problem.Status);
        Assert.False(string.IsNullOrWhiteSpace(problem.Title));
        return problem;
    }

    // ---- Seeding -----------------------------------------------------------------------------------------

    protected async Task<long> CreateProductAsync(string sku = "LAP-1", decimal price = 1000m, string name = "Laptop")
    {
        var response = await PostAsync("/api/products", new { sku, name, price });
        return (await ExpectAsync<ProductResponse>(response, HttpStatusCode.Created)).ProductId;
    }

    protected async Task<long> SeedWarehouseAsync(string name = "Dallas")
    {
        await using var db = _factory.CreateDbContext();
        var warehouse = new Warehouse { Name = name, Location = $"{name}, USA" };
        db.Warehouses.Add(warehouse);
        await db.SaveChangesAsync();
        return warehouse.WarehouseId;
    }

    protected async Task<long> SeedCustomerAsync(string name = "Ann Lee")
    {
        await using var db = _factory.CreateDbContext();
        var customer = new Customer { Name = name, Email = "ann@example.com" };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        return customer.CustomerId;
    }

    protected async Task ReceiveAsync(long productId, long warehouseId, int quantity)
    {
        var response = await PostAsync("/api/inventory/receipts", new { productId, warehouseId, quantity });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    protected async Task<OrderResponse> CreateOrderAsync(long customerId, long warehouseId, long productId, int quantity)
    {
        var response = await PostAsync("/api/orders", new
        {
            customerId,
            warehouseId,
            items = new[] { new { productId, quantity } }
        });
        return await ExpectAsync<OrderResponse>(response, HttpStatusCode.Created);
    }

    /// <summary>The current on-hand/reserved numbers, read through the API.</summary>
    protected async Task<InventoryItemResponse> GetStockAsync(long warehouseId, long productId)
    {
        var page = await ExpectAsync<PagedResponse<InventoryItemResponse>>(
            await Client.GetAsync($"/api/inventory?warehouseId={warehouseId}"), HttpStatusCode.OK);
        return page.Items.Single(i => i.ProductId == productId);
    }
}
