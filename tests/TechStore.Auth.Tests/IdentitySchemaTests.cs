using Microsoft.EntityFrameworkCore;
using TechStore.Auth.Tests.Infrastructure;

namespace TechStore.Auth.Tests;

/// <summary>The authentication tables the migration must create (see docs/diagrams/schema.dbml).</summary>
[Collection(AuthDatabaseCollection.Name)]
public class IdentitySchemaTests
{
    private readonly AuthDatabaseFixture _fixture;

    public IdentitySchemaTests(AuthDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<List<string>> ColumnsOfAsync(string table)
    {
        await using var db = _fixture.CreateContext();
        return await db.Database
            .SqlQuery<string>($"SELECT column_name AS \"Value\" FROM information_schema.columns WHERE table_schema = 'public' AND table_name = {table} ORDER BY column_name")
            .ToListAsync();
    }

    [Theory]
    [InlineData("users")]
    [InlineData("roles")]
    [InlineData("user_roles")]
    [InlineData("user_claims")]
    [InlineData("user_logins")]
    [InlineData("user_tokens")]
    [InlineData("role_claims")]
    public async Task IdentityTable_ExistsWithSnakeCaseName(string table)
    {
        Assert.NotEmpty(await ColumnsOfAsync(table));
    }

    [Fact]
    public async Task Users_HasTheDocumentedSnakeCaseColumns()
    {
        var columns = await ColumnsOfAsync("users");

        Assert.Equal(
            new[]
            {
                "access_failed_count", "concurrency_stamp", "display_name", "email", "email_confirmed", "id", "lockout_enabled",
                "lockout_end", "normalized_email", "normalized_user_name", "password_hash", "phone_number",
                "phone_number_confirmed", "security_stamp", "two_factor_enabled", "user_name"
            },
            columns);
    }

    [Fact]
    public async Task Users_HasUniqueIndexesOnEmailAndUserName()
    {
        await using var db = _fixture.CreateContext();

        var uniqueIndexes = await db.Database
            .SqlQuery<string>($"SELECT indexname AS \"Value\" FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'users' AND indexdef LIKE 'CREATE UNIQUE%' ORDER BY indexname")
            .ToListAsync();

        Assert.Contains("ux_users_normalized_email", uniqueIndexes);
        Assert.Contains("ux_users_normalized_user_name", uniqueIndexes);
    }

    [Fact]
    public async Task UserRoles_LinksUsersToRoles_WithAForeignKeyToEach()
    {
        await using var db = _fixture.CreateContext();

        var foreignKeys = await db.Database
            .SqlQuery<string>($"SELECT conname AS \"Value\" FROM pg_constraint WHERE contype = 'f' AND conrelid = 'public.user_roles'::regclass ORDER BY conname")
            .ToListAsync();

        Assert.Equal(new[] { "fk_user_roles_role", "fk_user_roles_user" }, foreignKeys);
    }

    [Fact]
    public async Task Identity_UsesBigintKeys_LikeTheRestOfTheSchema()
    {
        await using var db = _fixture.CreateContext();

        var types = await db.Database
            .SqlQuery<string>($"SELECT data_type AS \"Value\" FROM information_schema.columns WHERE table_schema = 'public' AND ((table_name = 'users' AND column_name = 'id') OR (table_name = 'roles' AND column_name = 'id') OR (table_name = 'user_roles' AND column_name IN ('user_id','role_id')))")
            .ToListAsync();

        Assert.Equal(4, types.Count);
        Assert.All(types, type => Assert.Equal("bigint", type));
    }

    [Fact]
    public async Task ExistingInventoryTables_AreStillThere()
    {
        foreach (var table in new[] { "products", "warehouses", "customers", "inventory_balances", "inventory_movements", "orders", "order_items" })
        {
            Assert.NotEmpty(await ColumnsOfAsync(table));
        }
    }
}
