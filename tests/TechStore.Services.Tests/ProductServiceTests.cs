using Microsoft.EntityFrameworkCore;
using TechStore.Dto;
using TechStore.Services.Exceptions;
using TechStore.Services.Tests.Infrastructure;

namespace TechStore.Services.Tests;

public class ProductServiceTests : DatabaseTestBase
{
    public ProductServiceTests(DatabaseFixture fixture) : base(fixture)
    {
    }

    /// <summary>Runs one service call with its own DbContext, the way one web request would.</summary>
    private async Task<T> RunAsync<T>(Func<ProductService, Task<T>> action)
    {
        await using var db = NewContext();
        return await action(new ProductService(db));
    }

    private async Task<int> CountProductsAsync()
    {
        await using var db = NewContext();
        return await db.Products.CountAsync();
    }

    // ======================================================================================================
    // CreateProduct
    // ======================================================================================================

    [Fact]
    public async Task CreateProduct_ValidRequest_SavesActiveProductWithGeneratedId()
    {
        var response = await RunAsync(s => s.CreateProductAsync(new CreateProductRequest("LAP-1", "Laptop", 1299.99m)));

        Assert.True(response.ProductId > 0);
        Assert.Equal("LAP-1", response.Sku);
        Assert.Equal("Laptop", response.Name);
        Assert.Equal(1299.99m, response.Price);
        Assert.True(response.IsActive);
        Assert.Equal(1, await CountProductsAsync());
    }

    [Fact]
    public async Task CreateProduct_TrimsAndUpperCasesSku()
    {
        var response = await RunAsync(s => s.CreateProductAsync(new CreateProductRequest("  lap-1 ", "  Laptop  ", 10m)));

        Assert.Equal("LAP-1", response.Sku);
        Assert.Equal("Laptop", response.Name);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    public async Task CreateProduct_SkuAtLengthLimits_Succeeds(int length)
    {
        var response = await RunAsync(s => s.CreateProductAsync(new CreateProductRequest(new string('A', length), "Item", 5m)));

        Assert.Equal(length, response.Sku.Length);
    }

    [Fact]
    public async Task CreateProduct_SkuLongerThan50_IsRejected()
    {
        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() =>
            RunAsync(s => s.CreateProductAsync(new CreateProductRequest(new string('A', 51), "Item", 5m))));

        Assert.Contains("sku", ex.Errors.Keys);
        Assert.Equal(0, await CountProductsAsync());
    }

    [Theory]
    [InlineData(255, true)]
    [InlineData(256, false)]
    public async Task CreateProduct_NameLengthBoundary(int length, bool allowed)
    {
        var request = new CreateProductRequest("SKU-1", new string('N', length), 5m);

        if (allowed)
        {
            var response = await RunAsync(s => s.CreateProductAsync(request));
            Assert.Equal(length, response.Name.Length);
        }
        else
        {
            var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => RunAsync(s => s.CreateProductAsync(request)));
            Assert.Contains("name", ex.Errors.Keys);
        }
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("0.01", true)]
    [InlineData("9999999999.99", true)]
    [InlineData("10000000000.00", false)]
    [InlineData("-0.01", false)]
    [InlineData("10.001", false)]
    public async Task CreateProduct_PriceBoundary(string priceText, bool allowed)
    {
        var price = decimal.Parse(priceText, System.Globalization.CultureInfo.InvariantCulture);
        var request = new CreateProductRequest("SKU-1", "Item", price);

        if (allowed)
        {
            var response = await RunAsync(s => s.CreateProductAsync(request));
            Assert.Equal(price, response.Price);
        }
        else
        {
            var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => RunAsync(s => s.CreateProductAsync(request)));
            Assert.Contains("price", ex.Errors.Keys);
            Assert.Equal(0, await CountProductsAsync());
        }
    }

    [Theory]
    [InlineData("", "Item")]
    [InlineData("   ", "Item")]
    [InlineData("SKU-1", "")]
    [InlineData("SKU-1", "   ")]
    public async Task CreateProduct_BlankSkuOrName_IsRejected(string sku, string name)
    {
        await Assert.ThrowsAsync<ValidationFailedException>(() =>
            RunAsync(s => s.CreateProductAsync(new CreateProductRequest(sku, name, 5m))));

        Assert.Equal(0, await CountProductsAsync());
    }

    [Fact]
    public async Task CreateProduct_DuplicateSku_IsConflictEvenWithDifferentCase()
    {
        await RunAsync(s => s.CreateProductAsync(new CreateProductRequest("LAP-1", "Laptop", 10m)));

        await Assert.ThrowsAsync<ConflictException>(() =>
            RunAsync(s => s.CreateProductAsync(new CreateProductRequest("lap-1", "Other", 20m))));

        Assert.Equal(1, await CountProductsAsync());
    }

    [Fact]
    public async Task CreateProduct_ConcurrentRequestsWithSameSku_ExactlyOneSucceeds()
    {
        var attempts = Enumerable.Range(0, 10)
            .Select(i => Task.Run(async () =>
            {
                try
                {
                    await RunAsync(s => s.CreateProductAsync(new CreateProductRequest("RACE-1", $"Racer {i}", 10m)));
                    return true;
                }
                catch (ConflictException)
                {
                    return false;
                }
            }));

        var results = await Task.WhenAll(attempts);

        Assert.Equal(1, results.Count(succeeded => succeeded));
        Assert.Equal(1, await CountProductsAsync());
    }

    // ======================================================================================================
    // ListProducts
    // ======================================================================================================

    [Fact]
    public async Task ListProducts_ReturnsProductsOrderedBySku()
    {
        await SeedProductAsync("C-3");
        await SeedProductAsync("A-1");
        await SeedProductAsync("B-2");

        var result = await RunAsync(s => s.ListProductsAsync());

        Assert.Equal(new[] { "A-1", "B-2", "C-3" }, result.Items.Select(p => p.Sku));
        Assert.Equal(3, result.TotalCount);
        Assert.Equal(1, result.Page);
        Assert.Equal(50, result.PageSize);
    }

    [Fact]
    public async Task ListProducts_ActiveOnly_HidesInactiveProducts()
    {
        await SeedProductAsync("ACTIVE-1", isActive: true);
        await SeedProductAsync("OLD-1", isActive: false);

        var all = await RunAsync(s => s.ListProductsAsync(activeOnly: false));
        var active = await RunAsync(s => s.ListProductsAsync(activeOnly: true));

        Assert.Equal(2, all.TotalCount);
        Assert.Single(active.Items);
        Assert.Equal("ACTIVE-1", active.Items[0].Sku);
        Assert.Equal(1, active.TotalCount);
    }

    [Fact]
    public async Task ListProducts_EmptyTable_ReturnsEmptyPage()
    {
        var result = await RunAsync(s => s.ListProductsAsync());

        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task ListProducts_PagesThroughResults_IncludingPartialLastPage()
    {
        for (var i = 1; i <= 5; i++)
        {
            await SeedProductAsync($"P-{i}");
        }

        var page1 = await RunAsync(s => s.ListProductsAsync(page: 1, pageSize: 2));
        var page3 = await RunAsync(s => s.ListProductsAsync(page: 3, pageSize: 2));
        var page4 = await RunAsync(s => s.ListProductsAsync(page: 4, pageSize: 2));

        Assert.Equal(new[] { "P-1", "P-2" }, page1.Items.Select(p => p.Sku));
        Assert.Equal(5, page1.TotalCount);
        Assert.Equal(new[] { "P-5" }, page3.Items.Select(p => p.Sku));
        Assert.Empty(page4.Items);
        Assert.Equal(5, page4.TotalCount);
    }

    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(1, 200, true)]
    [InlineData(0, 50, false)]
    [InlineData(1, 0, false)]
    [InlineData(1, 201, false)]
    [InlineData(-1, 50, false)]
    public async Task ListProducts_PagingBoundaries(int page, int pageSize, bool allowed)
    {
        if (allowed)
        {
            var result = await RunAsync(s => s.ListProductsAsync(page: page, pageSize: pageSize));
            Assert.Equal(pageSize, result.PageSize);
        }
        else
        {
            await Assert.ThrowsAsync<ValidationFailedException>(() =>
                RunAsync(s => s.ListProductsAsync(page: page, pageSize: pageSize)));
        }
    }

    // ======================================================================================================
    // UpdateProduct
    // ======================================================================================================

    [Fact]
    public async Task UpdateProduct_ChangesOnlySuppliedFields()
    {
        var id = await SeedProductAsync("LAP-1", price: 100m, name: "Laptop");

        var response = await RunAsync(s => s.UpdateProductAsync(id, new UpdateProductRequest(null, "Laptop 15", null, null)));

        Assert.Equal("Laptop 15", response.Name);
        Assert.Equal("LAP-1", response.Sku);
        Assert.Equal(100m, response.Price);
        Assert.True(response.IsActive);
    }

    [Fact]
    public async Task UpdateProduct_AllFieldsAtOnce_ArePersisted()
    {
        var id = await SeedProductAsync("LAP-1", price: 100m);

        await RunAsync(s => s.UpdateProductAsync(id, new UpdateProductRequest(" lap-2 ", "New name", 250.5m, false)));

        await using var db = NewContext();
        var saved = await db.Products.AsNoTracking().SingleAsync(p => p.ProductId == id);
        Assert.Equal("LAP-2", saved.Sku);
        Assert.Equal("New name", saved.Name);
        Assert.Equal(250.5m, saved.Price);
        Assert.False(saved.IsActive);
    }

    [Fact]
    public async Task UpdateProduct_DeactivateThenReactivate()
    {
        var id = await SeedProductAsync("LAP-1");

        var deactivated = await RunAsync(s => s.UpdateProductAsync(id, new UpdateProductRequest(null, null, null, false)));
        var reactivated = await RunAsync(s => s.UpdateProductAsync(id, new UpdateProductRequest(null, null, null, true)));

        Assert.False(deactivated.IsActive);
        Assert.True(reactivated.IsActive);
    }

    [Fact]
    public async Task UpdateProduct_SettingSameValues_Succeeds_AndOwnSkuIsNotAConflict()
    {
        var id = await SeedProductAsync("LAP-1", price: 100m, name: "Laptop");

        var response = await RunAsync(s => s.UpdateProductAsync(id, new UpdateProductRequest("LAP-1", "Laptop", 100m, true)));

        Assert.Equal("LAP-1", response.Sku);
    }

    [Fact]
    public async Task UpdateProduct_NoFieldsSupplied_IsRejected()
    {
        var id = await SeedProductAsync();

        await Assert.ThrowsAsync<ValidationFailedException>(() =>
            RunAsync(s => s.UpdateProductAsync(id, new UpdateProductRequest(null, null, null, null))));
    }

    [Theory]
    [InlineData("", null, null)]
    [InlineData(null, "  ", null)]
    [InlineData(null, null, "-1")]
    [InlineData(null, null, "1.234")]
    public async Task UpdateProduct_InvalidField_IsRejected_AndNothingChanges(string? sku, string? name, string? priceText)
    {
        var id = await SeedProductAsync("LAP-1", price: 100m, name: "Laptop");
        decimal? price = priceText is null ? null : decimal.Parse(priceText, System.Globalization.CultureInfo.InvariantCulture);

        await Assert.ThrowsAsync<ValidationFailedException>(() =>
            RunAsync(s => s.UpdateProductAsync(id, new UpdateProductRequest(sku, name, price, null))));

        await using var db = NewContext();
        var saved = await db.Products.AsNoTracking().SingleAsync(p => p.ProductId == id);
        Assert.Equal("LAP-1", saved.Sku);
        Assert.Equal("Laptop", saved.Name);
        Assert.Equal(100m, saved.Price);
    }

    [Fact]
    public async Task UpdateProduct_UnknownId_ThrowsNotFound()
    {
        await Assert.ThrowsAsync<NotFoundException>(() =>
            RunAsync(s => s.UpdateProductAsync(999, new UpdateProductRequest(null, "Name", null, null))));
    }

    [Fact]
    public async Task UpdateProduct_SkuOfAnotherProduct_ThrowsConflict_AndKeepsOriginal()
    {
        await SeedProductAsync("TAKEN-1");
        var id = await SeedProductAsync("MINE-1");

        await Assert.ThrowsAsync<ConflictException>(() =>
            RunAsync(s => s.UpdateProductAsync(id, new UpdateProductRequest("taken-1", null, null, null))));

        await using var db = NewContext();
        Assert.Equal("MINE-1", (await db.Products.AsNoTracking().SingleAsync(p => p.ProductId == id)).Sku);
    }

    [Fact]
    public async Task UpdateProduct_PriceChange_DoesNotChangeExistingOrderPrices()
    {
        var productId = await SeedProductAsync("LAP-1", price: 100m);
        var warehouseId = await SeedWarehouseAsync();
        var customerId = await SeedCustomerAsync();
        var orderId = await SeedOrderAsync(customerId, warehouseId, TechStore.Database.Entities.OrderStatus.Draft, (productId, 2, 100m));

        await RunAsync(s => s.UpdateProductAsync(productId, new UpdateProductRequest(null, null, 150m, null)));

        await using var db = NewContext();
        var item = await db.OrderItems.AsNoTracking().SingleAsync(i => i.OrderId == orderId);
        Assert.Equal(100m, item.UnitPrice);
    }

    // <next-tests>
}
