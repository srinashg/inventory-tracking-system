using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using TechStore.Database.Entities;

namespace TechStore.Database.Configurations;

/// <summary>
/// Gives the ASP.NET Core Identity tables the same snake_case naming as the rest of the schema
/// (users, roles, user_roles, ...) and makes the e-mail index unique at the database level.
/// </summary>
public static class IdentityModelConfiguration
{
    private static readonly Dictionary<Type, string> TableNames = new()
    {
        [typeof(AppUser)] = "users",
        [typeof(AppRole)] = "roles",
        [typeof(IdentityUserRole<long>)] = "user_roles",
        [typeof(IdentityUserClaim<long>)] = "user_claims",
        [typeof(IdentityUserLogin<long>)] = "user_logins",
        [typeof(IdentityUserToken<long>)] = "user_tokens",
        [typeof(IdentityRoleClaim<long>)] = "role_claims",
    };

    // The word used for a table in constraint names: fk_user_roles_user, fk_role_claims_role.
    private static readonly Dictionary<string, string> Singular = new() { ["users"] = "user", ["roles"] = "role" };

    public static void ConfigureIdentityTables(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppUser>(user =>
        {
            user.Property(u => u.DisplayName).HasMaxLength(255).HasDefaultValue(string.Empty);

            // Identity already declares these indexes; naming them (and making e-mail unique) here
            // updates the existing definitions instead of adding new ones.
            user.HasIndex(u => u.NormalizedEmail).IsUnique().HasDatabaseName("ux_users_normalized_email");
            user.HasIndex(u => u.NormalizedUserName).IsUnique().HasDatabaseName("ux_users_normalized_user_name");
        });

        modelBuilder.Entity<AppRole>(role =>
            role.HasIndex(r => r.NormalizedName).IsUnique().HasDatabaseName("ux_roles_normalized_name"));

        foreach (var entity in modelBuilder.Model.GetEntityTypes().Where(e => TableNames.ContainsKey(e.ClrType)))
        {
            var table = TableNames[entity.ClrType];
            entity.SetTableName(table);

            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.Name));
            }

            entity.FindPrimaryKey()?.SetName($"pk_{table}");

            foreach (var index in entity.GetIndexes().Where(i => i.GetDatabaseName()?.StartsWith("IX_") == true))
            {
                index.SetDatabaseName($"ix_{table}_{string.Join("_", index.Properties.Select(p => ToSnakeCase(p.Name)))}");
            }

            foreach (var foreignKey in entity.GetForeignKeys())
            {
                var principalTable = TableNames[foreignKey.PrincipalEntityType.ClrType];
                foreignKey.SetConstraintName($"fk_{table}_{Singular[principalTable]}");
            }
        }
    }

    /// <summary>"NormalizedUserName" becomes "normalized_user_name".</summary>
    private static string ToSnakeCase(string name) =>
        Regex.Replace(name, "(?<=[a-z0-9])(?=[A-Z])", "_").ToLowerInvariant();
}
