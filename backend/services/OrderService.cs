using Microsoft.EntityFrameworkCore;
using TechStore.Database;
using TechStore.Database.Entities;
using TechStore.Dto;
using TechStore.Services.Exceptions;
using TechStore.Services.Validation;

namespace TechStore.Services;

/// <summary>Creates draft orders and reads orders. Neither operation touches stock.</summary>
public class OrderService
{
    private const int MaxLines = 100;

    // "Reserved", "reserved" and "RESERVED" all find OrderStatus.Reserved; numbers such as "1" are not names, so they miss.
    private static readonly Dictionary<string, OrderStatus> StatusesByName =
        Enum.GetValues<OrderStatus>().ToDictionary(status => status.ToString(), status => status, StringComparer.OrdinalIgnoreCase);

    private readonly TechStoreDbContext _db;

    public OrderService(TechStoreDbContext db)
    {
        _db = db;
    }

    public async Task<OrderResponse> CreateDraftOrderAsync(CreateOrderRequest request)
    {
        var lines = request.Items ?? Array.Empty<OrderLineRequest>();
        ValidateRequest(request, lines);

        // Look up everything the order points at; any missing record rejects the whole order.
        if (!await _db.Customers.AnyAsync(c => c.CustomerId == request.CustomerId))
        {
            throw new NotFoundException($"Customer {request.CustomerId} was not found.");
        }

        if (!await _db.Warehouses.AnyAsync(w => w.WarehouseId == request.WarehouseId))
        {
            throw new NotFoundException($"Warehouse {request.WarehouseId} was not found.");
        }

        var productIds = lines.Select(l => l.ProductId).ToList();
        var products = await _db.Products.AsNoTracking()
            .Where(p => productIds.Contains(p.ProductId))
            .ToDictionaryAsync(p => p.ProductId);

        var missing = productIds.Where(id => !products.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            throw new NotFoundException($"Product(s) not found: {string.Join(", ", missing)}.");
        }

        var inactive = products.Values.Where(p => !p.IsActive).Select(p => p.Sku).OrderBy(sku => sku).ToList();
        if (inactive.Count > 0)
        {
            throw new ConflictException($"Inactive product(s) cannot be ordered: {string.Join(", ", inactive)}.");
        }

        var order = new Order
        {
            CustomerId = request.CustomerId,
            WarehouseId = request.WarehouseId,
            Status = OrderStatus.Draft
        };

        foreach (var line in lines)
        {
            // The unit price is copied now, so a later product price change does not alter this order.
            order.Items.Add(new OrderItem
            {
                ProductId = line.ProductId,
                Quantity = line.Quantity,
                UnitPrice = products[line.ProductId].Price
            });
        }

        // INSERT orders + INSERT order_items in the one implicit transaction of SaveChangesAsync:
        // either the order and all its lines are saved, or none are. No stock table is touched.
        _db.Orders.Add(order);
        await _db.SaveChangesAsync();

        return await OrderQueries.LoadResponseAsync(_db, order.OrderId);
    }

    public async Task<OrderResponse> GetOrderAsync(long orderId)
    {
        Rules.RequireId(orderId, "orderId");

        return await OrderQueries.LoadResponseAsync(_db, orderId);
    }

    public async Task<PagedResponse<OrderListItem>> ListOrdersAsync(
        IReadOnlyCollection<string>? statuses = null, long? customerId = null, long? warehouseId = null, int page = 1, int pageSize = 50)
    {
        var errors = new ValidationErrors();
        Rules.Paging(page, pageSize, errors);
        if (customerId is { } customer) Rules.Id(customer, "customerId", errors);
        if (warehouseId is { } warehouse) Rules.Id(warehouse, "warehouseId", errors);
        var wantedStatuses = ParseStatuses(statuses, errors);
        errors.ThrowIfAny();

        // Each filter that was supplied narrows the list (AND); several statuses are alternatives (OR).
        var query = _db.Orders.AsNoTracking();
        if (wantedStatuses.Count > 0) query = query.Where(o => wantedStatuses.Contains(o.Status));
        if (customerId is { } customerFilter) query = query.Where(o => o.CustomerId == customerFilter);
        if (warehouseId is { } warehouseFilter) query = query.Where(o => o.WarehouseId == warehouseFilter);

        // Two SELECTs: COUNT(*) for the total, then one page. The unit and amount totals are summed from each
        // order's lines inside that same statement, so there is no extra query per order. OrderId only ever
        // grows, so "newest first" is simply highest id first (and uses the primary-key index).
        var totalCount = await query.CountAsync();
        var rows = await query
            .OrderByDescending(o => o.OrderId)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(o => new
            {
                o.OrderId,
                o.CustomerId,
                o.WarehouseId,
                o.Status,
                o.CreatedAt,
                TotalUnits = o.Items.Sum(i => i.Quantity),
                TotalAmount = o.Items.Sum(i => i.Quantity * i.UnitPrice)
            })
            .ToListAsync();

        var items = rows
            .Select(r => new OrderListItem(r.OrderId, r.CustomerId, r.WarehouseId, r.Status.ToString(), r.CreatedAt, r.TotalUnits, r.TotalAmount))
            .ToList();

        return new PagedResponse<OrderListItem>(items, page, pageSize, totalCount);
    }

    /// <summary>Turns status names such as "reserved" into <see cref="OrderStatus"/> values. Numbers and unknown names are rejected.</summary>
    private static List<OrderStatus> ParseStatuses(IReadOnlyCollection<string>? names, ValidationErrors errors)
    {
        var result = new List<OrderStatus>();
        foreach (var name in names ?? Array.Empty<string>())
        {
            if (name is not null && StatusesByName.TryGetValue(name.Trim(), out var status))
            {
                result.Add(status);
            }
            else
            {
                errors.Add("status", $"'{name}' is not an order status. Use one of: {string.Join(", ", Enum.GetNames<OrderStatus>())}.");
            }
        }

        return result;
    }

    private static void ValidateRequest(CreateOrderRequest request, IReadOnlyList<OrderLineRequest> lines)
    {
        var errors = new ValidationErrors();
        Rules.Id(request.CustomerId, "customerId", errors);
        Rules.Id(request.WarehouseId, "warehouseId", errors);

        if (lines.Count is 0 or > MaxLines)
        {
            errors.Add("items", $"An order needs between 1 and {MaxLines} items.");
        }

        foreach (var line in lines)
        {
            Rules.Id(line.ProductId, "items.productId", errors);
            Rules.Quantity(line.Quantity, errors);
        }

        if (lines.GroupBy(l => l.ProductId).Any(g => g.Count() > 1))
        {
            errors.Add("items", "A product may appear on only one line.");
        }

        errors.ThrowIfAny();
    }
}
