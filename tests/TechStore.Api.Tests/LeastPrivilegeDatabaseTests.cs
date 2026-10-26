using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TechStore.Api.Tests.Infrastructure;
using TechStore.Auth;
using TechStore.Database;
using TechStore.Dto;

namespace TechStore.Api.Tests;

/// <summary>
/// Creates a database of its own ("techstore_api_priv_test"), migrates it as the owner, and runs backend/database/Provisioning/app-role.sql
/// to create a limited application role. The role is named techstore_app_test (not techstore_app) and is removed afterwards, so
/// these tests never touch the development database or its roles.
/// </summary>
public sealed class PrivilegedDatabaseFixture : IAsyncLifetime
{
    public const string RoleName = "techstore_app_test";

    public string OwnerConnectionString { get; } =
        Environment.GetEnvironmentVariable("TECHSTORE_API_PRIV_TEST_DB")
        ?? "Host=localhost;Port=5432;Database=techstore_api_priv_test;Username=postgres;Maximum Pool Size=20";

    public string AppPassword { get; private set; } = NewPassword();

    public static string NewPassword() => $"app-{Guid.NewGuid():N}-{Guid.NewGuid():N}";

    public string AppConnectionString(string? password = null)
    {
        var builder = new NpgsqlConnectionStringBuilder(OwnerConnectionString) { Username = RoleName, Password = password ?? AppPassword, Pooling = false };
        return builder.ConnectionString;
    }

    public async Task InitializeAsync()
    {
        var database = new NpgsqlConnectionStringBuilder(OwnerConnectionString).Database!;
        var admin = new NpgsqlConnectionStringBuilder(OwnerConnectionString) { Database = "postgres", Pooling = false };
        await using (var connection = new NpgsqlConnection(admin.ConnectionString))
        {
            await connection.OpenAsync();
            await using var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", connection);
            exists.Parameters.AddWithValue("name", database);
            if (await exists.ExecuteScalarAsync() is null)
            {
                await new NpgsqlCommand($"CREATE DATABASE \"{database}\"", connection).ExecuteNonQueryAsync();
            }
        }

        await using (var db = new TechStoreDbContext(new DbContextOptionsBuilder<TechStoreDbContext>().UseNpgsql(OwnerConnectionString).Options))
        {
            await db.Database.MigrateAsync(); // as the owner, exactly like the migrate container
        }

        await ProvisionAsync(AppPassword);
    }

    public async Task DisposeAsync()
    {
        await using var owner = await OpenOwnerAsync();
        await new NpgsqlCommand($"DROP OWNED BY {RoleName}", owner).ExecuteNonQueryAsync();
        await new NpgsqlCommand($"DROP ROLE IF EXISTS {RoleName}", owner).ExecuteNonQueryAsync();
    }

    public async Task<NpgsqlConnection> OpenOwnerAsync()
    {
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(OwnerConnectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    public async Task<NpgsqlConnection> OpenAppAsync(string? password = null)
    {
        var connection = new NpgsqlConnection(AppConnectionString(password));
        await connection.OpenAsync();
        return connection;
    }

    /// <summary>Runs app-role.sql as the owner. Passing null leaves the password setting unset, to test the safety check.</summary>
    public async Task ProvisionAsync(string? password)
    {
        await using var owner = await OpenOwnerAsync();
        if (password is not null)
        {
            await using var settings = new NpgsqlCommand("SELECT set_config('techstore.app_password', @p, false), set_config('techstore.app_role', @r, false)", owner);
            settings.Parameters.AddWithValue("p", password);
            settings.Parameters.AddWithValue("r", RoleName);
            await settings.ExecuteNonQueryAsync();
        }
        else
        {
            await new NpgsqlCommand($"SELECT set_config('techstore.app_role', '{RoleName}', false)", owner).ExecuteNonQueryAsync();
        }

        await new NpgsqlCommand(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "app-role.sql")), owner).ExecuteNonQueryAsync();
        if (password is not null) AppPassword = password;
    }
}

public class LeastPrivilegeDatabaseTests : IClassFixture<PrivilegedDatabaseFixture>
{
    private readonly PrivilegedDatabaseFixture _db;

    public LeastPrivilegeDatabaseTests(PrivilegedDatabaseFixture db)
    {
        _db = db;
    }

    private static async Task<T?> ScalarAsync<T>(NpgsqlConnection connection, string sql) => (T?)await new NpgsqlCommand(sql, connection).ExecuteScalarAsync();

    private static Task ExecuteAsync(NpgsqlConnection connection, string sql) => new NpgsqlCommand(sql, connection).ExecuteNonQueryAsync();

    // ---- What the role is --------------------------------------------------------------------------------

    [Fact]
    public async Task TheApplicationRole_HasNoSpecialPowers()
    {
        await using var owner = await _db.OpenOwnerAsync();

        var flags = await new NpgsqlCommand(
            $"SELECT rolsuper, rolcreatedb, rolcreaterole, rolreplication, rolbypassrls, rolcanlogin FROM pg_roles WHERE rolname = '{PrivilegedDatabaseFixture.RoleName}'", owner)
            .ExecuteReaderAsync();
        await flags.ReadAsync();

        Assert.Equal(new[] { false, false, false, false, false, true }, Enumerable.Range(0, 6).Select(flags.GetBoolean).ToArray());
    }

    // ---- What it cannot do -------------------------------------------------------------------------------

    [Theory]
    [InlineData("CREATE TABLE sneaky (id int)")]
    [InlineData("DROP TABLE products")]
    [InlineData("DROP TABLE users CASCADE")]
    [InlineData("ALTER TABLE products ADD COLUMN sneaky int")]
    [InlineData("TRUNCATE products")]
    [InlineData("CREATE INDEX sneaky_ix ON products (sku)")]
    [InlineData("CREATE SCHEMA sneaky")]
    [InlineData("CREATE ROLE sneaky_role")]
    [InlineData("CREATE DATABASE sneaky_db")]
    [InlineData("ALTER ROLE techstore_app_test SUPERUSER")]
    [InlineData("COPY (SELECT 1) TO PROGRAM 'id'")]
    [InlineData("SELECT pg_read_file('/etc/passwd')")]
    [InlineData("CREATE EXTENSION IF NOT EXISTS pg_trgm")]
    public async Task TheApplicationRole_CannotChangeTheSchemaOrUseSuperuserFeatures(string sql)
    {
        await using var app = await _db.OpenAppAsync();

        var failure = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(app, sql));

        Assert.Equal("42501", failure.SqlState); // insufficient_privilege
    }

    [Theory]
    [InlineData("DELETE FROM products")]
    [InlineData("DELETE FROM orders")]
    [InlineData("DELETE FROM inventory_movements")]
    [InlineData("INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('x', 'y')")]
    [InlineData("DELETE FROM \"__EFMigrationsHistory\"")]
    [InlineData("UPDATE \"__EFMigrationsHistory\" SET \"ProductVersion\" = 'z'")]
    public async Task TheApplicationRole_CannotDeleteBusinessRecords_OrTamperWithTheMigrationHistory(string sql)
    {
        await using var app = await _db.OpenAppAsync();

        var failure = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(app, sql));

        Assert.Equal("42501", failure.SqlState);
    }

    // ---- What it can do ----------------------------------------------------------------------------------

    [Fact]
    public async Task TheApplicationRole_CanReadWriteAndUseSequences_AndReadTheMigrationHistory()
    {
        await using var app = await _db.OpenAppAsync();
        var name = "Priv-" + Guid.NewGuid().ToString("N")[..8];

        var id = await ScalarAsync<long>(app, $"INSERT INTO warehouses (name, location) VALUES ('{name}', 'x') RETURNING warehouse_id");
        await ExecuteAsync(app, $"UPDATE warehouses SET location = 'y' WHERE warehouse_id = {id}");

        Assert.Equal("y", await ScalarAsync<string>(app, $"SELECT location FROM warehouses WHERE warehouse_id = {id}"));
        Assert.True(await ScalarAsync<long>(app, "SELECT count(*) FROM \"__EFMigrationsHistory\"") > 0);
        Assert.True(await ScalarAsync<long>(app, "SELECT count(*) FROM users") >= 0);
    }

    [Fact]
    public async Task TheApplicationRole_CanReplaceUserRoles_TheOneTableItDeletesFrom()
    {
        await using var app = await _db.OpenAppAsync();

        await ExecuteAsync(app, "DELETE FROM user_roles WHERE false");
    }

    // ---- Future migrations -------------------------------------------------------------------------------

    [Fact]
    public async Task ATableAddedByALaterMigration_IsUsableByTheApplicationRoleWithoutAnotherGrant()
    {
        await using (var owner = await _db.OpenOwnerAsync())
        {
            await ExecuteAsync(owner, "CREATE TABLE future_table (id serial PRIMARY KEY, note text)");
        }

        try
        {
            await using var app = await _db.OpenAppAsync();
            await ExecuteAsync(app, "INSERT INTO future_table (note) VALUES ('hello')"); // needs the table AND its sequence
            Assert.Equal("hello", await ScalarAsync<string>(app, "SELECT note FROM future_table"));
            await ExecuteAsync(app, "UPDATE future_table SET note = 'again'");
            var failure = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(app, "DROP TABLE future_table"));
            Assert.Equal("42501", failure.SqlState); // but it still cannot change the schema
        }
        finally
        {
            await using var owner = await _db.OpenOwnerAsync();
            await ExecuteAsync(owner, "DROP TABLE IF EXISTS future_table");
        }
    }

    // ---- Safe to repeat ----------------------------------------------------------------------------------

    [Fact]
    public async Task RunningTheProvisioningAgain_KeepsAllDataAndAccess()
    {
        var name = "Keep-" + Guid.NewGuid().ToString("N")[..8];
        await using (var owner = await _db.OpenOwnerAsync())
        {
            await ExecuteAsync(owner, $"INSERT INTO warehouses (name, location) VALUES ('{name}', 'x')");
        }

        await _db.ProvisionAsync(_db.AppPassword);
        await _db.ProvisionAsync(_db.AppPassword);

        await using var app = await _db.OpenAppAsync();
        Assert.Equal(1L, await ScalarAsync<long>(app, $"SELECT count(*) FROM warehouses WHERE name = '{name}'"));
        await ExecuteAsync(app, "UPDATE warehouses SET location = location WHERE false");
    }

    [Fact]
    public async Task RunningTheProvisioningWithANewPassword_ChangesThePassword_StoredOnlyAsAHash()
    {
        // (The local development server may allow any password on localhost, so the stored hash is checked instead of a failed login.)
        await using var owner = await _db.OpenOwnerAsync();
        var before = await ScalarAsync<string>(owner, $"SELECT rolpassword FROM pg_authid WHERE rolname = '{PrivilegedDatabaseFixture.RoleName}'");
        var newPassword = PrivilegedDatabaseFixture.NewPassword();

        await _db.ProvisionAsync(newPassword);

        var after = await ScalarAsync<string>(owner, $"SELECT rolpassword FROM pg_authid WHERE rolname = '{PrivilegedDatabaseFixture.RoleName}'");
        Assert.StartsWith("SCRAM-SHA-256$", after);
        Assert.NotEqual(before, after);
        Assert.DoesNotContain(newPassword, after);
        await using var app = await _db.OpenAppAsync(newPassword);
        Assert.Equal(1, await ScalarAsync<int>(app, "SELECT 1"));
    }

    [Fact]
    public async Task WithoutAPassword_TheProvisioningStops_AndChangesNothing()
    {
        var failure = await Assert.ThrowsAsync<PostgresException>(() => _db.ProvisionAsync(null));

        Assert.Contains("TECHSTORE_APP_DB_PASSWORD", failure.MessageText);
        await using var app = await _db.OpenAppAsync(); // the existing password still works
        Assert.Equal(1, await ScalarAsync<int>(app, "SELECT 1"));
    }

    // ---- The real application, connected as the limited role ---------------------------------------------

    [Fact]
    public async Task TheWholeApplication_WorksWhenConnectedAsTheLimitedRole()
    {
        var adminPassword = $"pw-{Guid.NewGuid():N}-{Guid.NewGuid():N}";
        var unique = Guid.NewGuid().ToString("N")[..8]; // accounts are never overwritten, so each run uses its own
        var adminEmail = $"priv-admin-{unique}@test.local";
        var salesEmail = $"priv-sales-{unique}@test.local";
        long warehouseId, customerId;
        await using (var owner = await _db.OpenOwnerAsync())
        {
            warehouseId = (await ScalarAsync<long>(owner, "INSERT INTO warehouses (name, location) VALUES ('Wf-" + Guid.NewGuid().ToString("N")[..6] + "', 'x') RETURNING warehouse_id"));
            customerId = (await ScalarAsync<long>(owner, "INSERT INTO customers (name, email) VALUES ('Wf Customer', 'wf-" + Guid.NewGuid().ToString("N")[..8] + "@example.test') RETURNING customer_id"));
        }

        await using var host = new ConfiguredApiFactory(_db.AppConnectionString(), "Testing", new Dictionary<string, string?>
        {
            ["Auth:RateLimit:LoginPerClientPerMinute"] = "100000",
            ["Auth:RateLimit:LoginGlobalPerMinute"] = "100000",
            ["Auth:TestAccounts:0:Email"] = adminEmail,
            ["Auth:TestAccounts:0:Password"] = adminPassword,
            ["Auth:TestAccounts:0:Role"] = "Admin",
            ["Auth:TestAccounts:1:Email"] = salesEmail,
            ["Auth:TestAccounts:1:Password"] = adminPassword,
            ["Auth:TestAccounts:1:Role"] = "Sales",
        });
        var client = host.Browser();
        var json = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);

        // Startup created the roles and accounts as the limited role; now sign in (updates the user row), and run the journey.
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login", new { email = adminEmail, password = adminPassword }, json)).StatusCode);

        var sku = "PRIV-" + Guid.NewGuid().ToString("N")[..6];
        var product = await (await client.PostAsJsonAsync("/api/products", new { sku, name = "Laptop", price = 1299.99 }, json)).Content.ReadFromJsonAsync<ProductResponse>(json);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/inventory/receipts", new { productId = product!.ProductId, warehouseId, quantity = 10 }, json)).StatusCode);
        var order = await (await client.PostAsJsonAsync("/api/orders", new { customerId, warehouseId, items = new[] { new { productId = product.ProductId, quantity = 2 } } }, json)).Content.ReadFromJsonAsync<OrderResponse>(json);
        foreach (var step in new[] { "submit", "pick", "pack", "ship" })
        {
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/orders/{order!.OrderId}/{step}", null)).StatusCode);
        }

        var stock = (await client.GetFromJsonAsync<PagedResponse<InventoryItemResponse>>($"/api/inventory?warehouseId={warehouseId}", json))!.Items.Single(i => i.ProductId == product.ProductId);
        Assert.Equal((8, 0, 8), (stock.OnHand, stock.Reserved, stock.Available));

        // Insufficient stock is refused and changes nothing.
        var tooMany = await (await client.PostAsJsonAsync("/api/orders", new { customerId, warehouseId, items = new[] { new { productId = product.ProductId, quantity = 15 } } }, json)).Content.ReadFromJsonAsync<OrderResponse>(json);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/orders/{tooMany!.OrderId}/submit", null)).StatusCode);
        stock = (await client.GetFromJsonAsync<PagedResponse<InventoryItemResponse>>($"/api/inventory?warehouseId={warehouseId}", json))!.Items.Single(i => i.ProductId == product.ProductId);
        Assert.Equal((8, 0, 8), (stock.OnHand, stock.Reserved, stock.Available));

        // Account operations that write: changing a role (deletes a user_roles row), locking, and logging out (security stamp).
        using var scope = host.Services.CreateScope();
        var accounts = scope.ServiceProvider.GetRequiredService<AccountService>();
        await using var db = scope.ServiceProvider.GetRequiredService<TechStoreDbContext>();
        var sales = await db.Users.SingleAsync(u => u.Email == salesEmail);
        await accounts.AssignRoleAsync(sales.Id, Roles.Warehouse);
        await accounts.SetLockedAsync(sales.Id, true);
        await accounts.SetLockedAsync(sales.Id, false);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/logout", null)).StatusCode);
    }
}
