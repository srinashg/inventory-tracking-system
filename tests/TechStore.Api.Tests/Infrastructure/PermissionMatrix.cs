namespace TechStore.Api.Tests.Infrastructure;

/// <summary>
/// The endpoint-by-role permission matrix from docs/api-contract.md section 2, written out for the tests.
/// "SignedIn" means any of the three roles; "Anonymous" means no session is needed.
/// </summary>
public record EndpointRule(string Method, string Template, string Path, string Access)
{
    public bool IsAnonymous => Access == "Anonymous";

    public bool Allows(string role) => Access switch
    {
        "SignedIn" => true,
        "ManageProducts" => role == "Admin",
        "ManageStock" => role is "Admin" or "Warehouse",
        "ManageOrders" => role is "Admin" or "Sales",
        "FulfillOrders" => role is "Admin" or "Warehouse",
        _ => false,
    };

    public override string ToString() => $"{Method} {Path}";
}

public static class PermissionMatrix
{
    public static readonly IReadOnlyList<EndpointRule> Rules = new[]
    {
        new EndpointRule("GET", "api/products", "/api/products", "SignedIn"),
        new EndpointRule("POST", "api/products", "/api/products", "ManageProducts"),
        new EndpointRule("PATCH", "api/products/{id:long}", "/api/products/1", "ManageProducts"),
        new EndpointRule("GET", "api/warehouses", "/api/warehouses", "SignedIn"),
        new EndpointRule("GET", "api/customers", "/api/customers", "SignedIn"),
        new EndpointRule("GET", "api/inventory", "/api/inventory?warehouseId=1", "SignedIn"),
        new EndpointRule("POST", "api/inventory/receipts", "/api/inventory/receipts", "ManageStock"),
        new EndpointRule("POST", "api/inventory/adjustments", "/api/inventory/adjustments", "ManageStock"),
        new EndpointRule("POST", "api/orders", "/api/orders", "ManageOrders"),
        new EndpointRule("GET", "api/orders", "/api/orders", "SignedIn"),
        new EndpointRule("GET", "api/orders/{id:long}", "/api/orders/1", "SignedIn"),
        new EndpointRule("POST", "api/orders/{id:long}/submit", "/api/orders/1/submit", "ManageOrders"),
        new EndpointRule("POST", "api/orders/{id:long}/pick", "/api/orders/1/pick", "FulfillOrders"),
        new EndpointRule("POST", "api/orders/{id:long}/pack", "/api/orders/1/pack", "FulfillOrders"),
        new EndpointRule("POST", "api/orders/{id:long}/ship", "/api/orders/1/ship", "FulfillOrders"),
        new EndpointRule("POST", "api/orders/{id:long}/cancel", "/api/orders/1/cancel", "ManageOrders"),
        new EndpointRule("GET", "api/dashboard/summary", "/api/dashboard/summary", "SignedIn"),
        new EndpointRule("GET", "health", "/health", "Anonymous"),
        new EndpointRule("GET", "api/auth/csrf", "/api/auth/csrf", "Anonymous"),
        new EndpointRule("POST", "api/auth/login", "/api/auth/login", "Anonymous"),
        new EndpointRule("POST", "api/auth/logout", "/api/auth/logout", "SignedIn"),
        new EndpointRule("GET", "api/auth/me", "/api/auth/me", "SignedIn"),
    };
}
