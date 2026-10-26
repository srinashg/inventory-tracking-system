using Microsoft.AspNetCore.Authorization;

namespace TechStore.Auth;

/// <summary>
/// The one place that says which roles may do what (docs/api-contract.md section 2.1).
/// Controllers refer to a policy by name and never mention a role.
/// </summary>
public static class AuthorizationSetup
{
    public static void Configure(AuthorizationOptions options)
    {
        // Secure by default: an endpoint with no explicit rule still needs a signed-in user.
        // Only endpoints marked [AllowAnonymous] (or AllowAnonymous() for /health) are open.
        options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();

        options.AddPolicy(Policies.ManageProducts, policy => policy.RequireRole(Roles.Admin));
        options.AddPolicy(Policies.ManageStock, policy => policy.RequireRole(Roles.Admin, Roles.Warehouse));
        options.AddPolicy(Policies.ManageOrders, policy => policy.RequireRole(Roles.Admin, Roles.Sales));
        options.AddPolicy(Policies.FulfillOrders, policy => policy.RequireRole(Roles.Admin, Roles.Warehouse));
    }
}
