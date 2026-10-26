using Microsoft.EntityFrameworkCore;
using TechStore.Database;
using TechStore.Dto;

namespace TechStore.Services;

/// <summary>Supplies the warehouse and customer choices shown on order and inventory forms.</summary>
public class ReferenceDataService
{
    private readonly TechStoreDbContext _db;

    public ReferenceDataService(TechStoreDbContext db)
    {
        _db = db;
    }

    // Both lists are small, so they are not paged. One read-only SELECT each.
    public async Task<IReadOnlyList<WarehouseResponse>> ListWarehousesAsync() =>
        await _db.Warehouses.AsNoTracking()
            .OrderBy(w => w.Name)
            .Select(w => new WarehouseResponse(w.WarehouseId, w.Name, w.Location))
            .ToListAsync();

    public async Task<IReadOnlyList<CustomerResponse>> ListCustomersAsync() =>
        await _db.Customers.AsNoTracking()
            .OrderBy(c => c.Name)
            .Select(c => new CustomerResponse(c.CustomerId, c.Name, c.Email))
            .ToListAsync();
}
