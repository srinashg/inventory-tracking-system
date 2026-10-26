using System.Net;
using TechStore.Api.Tests.Infrastructure;
using TechStore.Dto;

namespace TechStore.Api.Tests;

public class ProductsApiTests : ApiTestBase
{
    public ProductsApiTests(ApiFactory factory) : base(factory)
    {
    }

    // ---- POST /api/products ------------------------------------------------------------------------------

    [Fact]
    public async Task PostProduct_Valid_Returns201WithProduct()
    {
        var response = await PostAsync("/api/products", new { sku = "lap-1", name = "Laptop 14\"", price = 1299.99 });

        var product = await ExpectAsync<ProductResponse>(response, HttpStatusCode.Created);
        Assert.True(product.ProductId > 0);
        Assert.Equal("LAP-1", product.Sku);
        Assert.Equal("Laptop 14\"", product.Name);
        Assert.Equal(1299.99m, product.Price);
        Assert.True(product.IsActive);
    }

    [Fact]
    public async Task PostProduct_ResponseUsesCamelCaseProperties()
    {
        var response = await PostAsync("/api/products", new { sku = "A-1", name = "Thing", price = 1 });

        var raw = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"productId\"", raw);
        Assert.Contains("\"isActive\"", raw);
    }

    [Fact]
    public async Task PostProduct_DuplicateSku_Returns409Problem()
    {
        await CreateProductAsync("LAP-1");

        var problem = await ExpectProblemAsync(
            await PostAsync("/api/products", new { sku = "LAP-1", name = "Other", price = 5 }), HttpStatusCode.Conflict);

        Assert.Contains("LAP-1", problem.Detail);
    }

    [Fact]
    public async Task PostProduct_InvalidFields_Returns400WithFieldErrors()
    {
        var problem = await ExpectProblemAsync(
            await PostAsync("/api/products", new { sku = "", name = "Thing", price = -5 }), HttpStatusCode.BadRequest);

        Assert.NotNull(problem.Errors);
        Assert.Contains("sku", problem.Errors.Keys);
        Assert.Contains("price", problem.Errors.Keys);
        Assert.DoesNotContain("name", problem.Errors.Keys);
    }

    [Fact]
    public async Task PostProduct_MalformedJson_Returns400Problem()
    {
        var content = new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json");

        var response = await Client.PostAsync("/api/products", content);

        await ExpectProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostProduct_EmptyBody_Returns400Problem()
    {
        var response = await Client.PostAsync("/api/products", new StringContent("", System.Text.Encoding.UTF8, "application/json"));

        await ExpectProblemAsync(response, HttpStatusCode.BadRequest);
    }

    // ---- GET /api/products -------------------------------------------------------------------------------

    [Fact]
    public async Task GetProducts_ReturnsPagedListOrderedBySku()
    {
        await CreateProductAsync("B-2");
        await CreateProductAsync("A-1");

        var page = await ExpectAsync<PagedResponse<ProductResponse>>(await Client.GetAsync("/api/products"), HttpStatusCode.OK);

        Assert.Equal(new[] { "A-1", "B-2" }, page.Items.Select(p => p.Sku));
        Assert.Equal((1, 50, 2), (page.Page, page.PageSize, page.TotalCount));
    }

    [Fact]
    public async Task GetProducts_ActiveOnlyAndPagingParametersAreHonored()
    {
        await CreateProductAsync("A-1");
        var inactive = await CreateProductAsync("B-2");
        await CreateProductAsync("C-3");
        await ExpectAsync<ProductResponse>(await PatchAsync($"/api/products/{inactive}", new { isActive = false }), HttpStatusCode.OK);

        var active = await ExpectAsync<PagedResponse<ProductResponse>>(
            await Client.GetAsync("/api/products?activeOnly=true"), HttpStatusCode.OK);
        var secondPage = await ExpectAsync<PagedResponse<ProductResponse>>(
            await Client.GetAsync("/api/products?page=2&pageSize=2"), HttpStatusCode.OK);

        Assert.Equal(new[] { "A-1", "C-3" }, active.Items.Select(p => p.Sku));
        Assert.Equal(new[] { "C-3" }, secondPage.Items.Select(p => p.Sku));
        Assert.Equal(3, secondPage.TotalCount);
    }

    [Theory]
    [InlineData("/api/products?pageSize=0")]
    [InlineData("/api/products?pageSize=201")]
    [InlineData("/api/products?page=0")]
    public async Task GetProducts_InvalidPaging_Returns400(string url)
    {
        await ExpectProblemAsync(await Client.GetAsync(url), HttpStatusCode.BadRequest);
    }

    // ---- PATCH /api/products/{id} ------------------------------------------------------------------------

    [Fact]
    public async Task PatchProduct_UpdatesSuppliedFieldsOnly()
    {
        var id = await CreateProductAsync("LAP-1", price: 100m, name: "Laptop");

        var product = await ExpectAsync<ProductResponse>(
            await PatchAsync($"/api/products/{id}", new { price = 120.5, isActive = false }), HttpStatusCode.OK);

        Assert.Equal((id, "LAP-1", "Laptop", 120.5m, false),
            (product.ProductId, product.Sku, product.Name, product.Price, product.IsActive));
    }

    [Fact]
    public async Task PatchProduct_EmptyBody_Returns400()
    {
        var id = await CreateProductAsync();

        await ExpectProblemAsync(await PatchAsync($"/api/products/{id}", new { }), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PatchProduct_UnknownId_Returns404()
    {
        await ExpectProblemAsync(await PatchAsync("/api/products/999", new { name = "X" }), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PatchProduct_SkuOfAnotherProduct_Returns409()
    {
        await CreateProductAsync("TAKEN-1");
        var id = await CreateProductAsync("MINE-1");

        await ExpectProblemAsync(await PatchAsync($"/api/products/{id}", new { sku = "taken-1" }), HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task PatchProduct_NonNumericId_Returns404()
    {
        var response = await PatchAsync("/api/products/abc", new { name = "X" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
