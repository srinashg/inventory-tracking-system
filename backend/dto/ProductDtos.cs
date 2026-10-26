namespace TechStore.Dto;

public record CreateProductRequest(string Sku, string Name, decimal Price);

/// <summary>PATCH body: only the fields that are supplied (not null) are changed.</summary>
public record UpdateProductRequest(string? Sku, string? Name, decimal? Price, bool? IsActive);

public record ProductResponse(long ProductId, string Sku, string Name, decimal Price, bool IsActive);
