namespace TechStore.Dto;

public record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public record WarehouseResponse(long WarehouseId, string Name, string Location);

public record CustomerResponse(long CustomerId, string Name, string Email);
