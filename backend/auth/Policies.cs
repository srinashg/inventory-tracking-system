namespace TechStore.Auth;

/// <summary>
/// The four capability policies. A controller says what it protects ([Authorize(Policy = Policies.ManageStock)]);
/// <see cref="AuthorizationSetup"/> says which roles may do it. Reading data needs only a signed-in user.
/// </summary>
public static class Policies
{
    public const string ManageProducts = nameof(ManageProducts);
    public const string ManageStock = nameof(ManageStock);
    public const string ManageOrders = nameof(ManageOrders);
    public const string FulfillOrders = nameof(FulfillOrders);

    public static readonly IReadOnlyList<string> All = new[] { ManageProducts, ManageStock, ManageOrders, FulfillOrders };
}
