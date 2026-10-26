namespace TechStore.Auth;

/// <summary>The three roles an employee can have. See docs/api-contract.md section 2.1.</summary>
public static class Roles
{
    public const string Admin = "Admin";
    public const string Sales = "Sales";
    public const string Warehouse = "Warehouse";

    public static readonly IReadOnlyList<string> All = new[] { Admin, Sales, Warehouse };
}
