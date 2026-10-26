using Microsoft.EntityFrameworkCore;
using TechStore.Database;
using TechStore.Database.Entities;
using TechStore.Dto;
using TechStore.Services.Exceptions;
using TechStore.Services.Validation;

namespace TechStore.Services;

/// <summary>Creates, lists and edits catalog products.</summary>
public class ProductService
{
    private readonly TechStoreDbContext _db;

    public ProductService(TechStoreDbContext db)
    {
        _db = db;
    }

    public async Task<ProductResponse> CreateProductAsync(CreateProductRequest request)
    {
        var errors = new ValidationErrors();
        var sku = Rules.Sku(request.Sku, errors);
        var name = Rules.Name(request.Name, errors);
        Rules.Price(request.Price, errors);
        errors.ThrowIfAny();

        var product = new Product { Sku = sku, Name = name, Price = request.Price, IsActive = true };
        _db.Products.Add(product);

        try
        {
            // One INSERT in an implicit transaction. The unique index on sku is the final judge of duplicates,
            // so two simultaneous requests cannot both succeed.
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            throw new ConflictException($"A product with SKU '{sku}' already exists.");
        }

        return ToResponse(product);
    }

    public async Task<PagedResponse<ProductResponse>> ListProductsAsync(bool activeOnly = false, int page = 1, int pageSize = 50)
    {
        var errors = new ValidationErrors();
        Rules.Paging(page, pageSize, errors);
        errors.ThrowIfAny();

        // AsNoTracking: this is a read-only query, so EF Core skips change tracking.
        var query = _db.Products.AsNoTracking();
        if (activeOnly)
        {
            query = query.Where(p => p.IsActive);
        }

        // Two SELECTs: COUNT(*) for the total, then one page ordered by SKU (LIMIT/OFFSET).
        var totalCount = await query.CountAsync();
        var items = await query
            .OrderBy(p => p.Sku)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new ProductResponse(p.ProductId, p.Sku, p.Name, p.Price, p.IsActive))
            .ToListAsync();

        return new PagedResponse<ProductResponse>(items, page, pageSize, totalCount);
    }

    public async Task<ProductResponse> UpdateProductAsync(long productId, UpdateProductRequest request)
    {
        var errors = new ValidationErrors();
        if (request.Sku is null && request.Name is null && request.Price is null && request.IsActive is null)
        {
            errors.Add("body", "Supply at least one field to change.");
        }

        // Only validate the fields that were supplied; a null field means "leave unchanged".
        var sku = request.Sku is null ? null : Rules.Sku(request.Sku, errors);
        var name = request.Name is null ? null : Rules.Name(request.Name, errors);
        if (request.Price is { } price)
        {
            Rules.Price(price, errors);
        }

        errors.ThrowIfAny();

        // SELECT the product; EF Core tracks it, so SaveChanges later UPDATEs only the columns that changed.
        var product = await _db.Products.SingleOrDefaultAsync(p => p.ProductId == productId)
            ?? throw new NotFoundException($"Product {productId} was not found.");

        if (sku is not null) product.Sku = sku;
        if (name is not null) product.Name = name;
        if (request.Price is { } newPrice) product.Price = newPrice;
        if (request.IsActive is { } isActive) product.IsActive = isActive;

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            throw new ConflictException($"A product with SKU '{sku}' already exists.");
        }

        return ToResponse(product);
    }

    private static ProductResponse ToResponse(Product p) => new(p.ProductId, p.Sku, p.Name, p.Price, p.IsActive);
}
