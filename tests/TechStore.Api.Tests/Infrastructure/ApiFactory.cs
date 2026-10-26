using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TechStore.Database;

namespace TechStore.Api.Tests.Infrastructure;

/// <summary>A test employee. The password is random for every test run, so no password is ever committed.</summary>
public record TestAccount(string Email, string Password, string Role);

/// <summary>
/// Starts the real API in memory (real routing, middleware, controllers, services and EF Core) against a
/// separate PostgreSQL database, "techstore_api_test", so these tests never touch development data or the
/// service tests' database. It runs in the "Testing" environment, which is the only one where test accounts exist.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private static string RandomPassword() => $"pw-{Guid.NewGuid():N}-{Guid.NewGuid():N}";

    /// <summary>One account per role. They exist only because the API runs in the "Testing" environment.</summary>
    public IReadOnlyList<TestAccount> Accounts { get; } = new[]
    {
        new TestAccount("admin@test.local", RandomPassword(), "Admin"),
        new TestAccount("sales@test.local", RandomPassword(), "Sales"),
        new TestAccount("warehouse@test.local", RandomPassword(), "Warehouse"),
    };

    public TestAccount Account(string role) => Accounts.Single(a => a.Role == role);

    public const string DefaultConnection =
        "Host=localhost;Port=5432;Database=techstore_api_test;Username=postgres;Maximum Pool Size=30";

    public string ConnectionString { get; } =
        Environment.GetEnvironmentVariable("TECHSTORE_API_TEST_DB") ?? DefaultConnection;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:TechStore", ConnectionString);

        // The suite signs in hundreds of times from one address; the rate limiter itself is tested with its own settings.
        builder.UseSetting("Auth:RateLimit:LoginPerClientPerMinute", "100000");
        builder.UseSetting("Auth:RateLimit:LoginGlobalPerMinute", "100000");

        for (var i = 0; i < Accounts.Count; i++)
        {
            builder.UseSetting($"Auth:TestAccounts:{i}:Email", Accounts[i].Email);
            builder.UseSetting($"Auth:TestAccounts:{i}:Password", Accounts[i].Password);
            builder.UseSetting($"Auth:TestAccounts:{i}:Role", Accounts[i].Role);
        }
    }

    public async Task InitializeAsync()
    {
        await CreateDatabaseIfMissingAsync();

        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
    }

    Task IAsyncLifetime.DisposeAsync() => Task.CompletedTask;

    /// <summary>A DbContext for seeding and inspecting data directly, outside the API.</summary>
    public TechStoreDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<TechStoreDbContext>().UseNpgsql(ConnectionString).Options);

    private async Task CreateDatabaseIfMissingAsync()
    {
        var databaseName = new NpgsqlConnectionStringBuilder(ConnectionString).Database!;
        var admin = new NpgsqlConnectionStringBuilder(ConnectionString) { Database = "postgres", Pooling = false };

        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync();

        await using var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", connection);
        exists.Parameters.AddWithValue("name", databaseName);

        if (await exists.ExecuteScalarAsync() is null)
        {
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", connection);
            await create.ExecuteNonQueryAsync();
        }
    }
}

[CollectionDefinition(Name)]
public class ApiCollection : ICollectionFixture<ApiFactory>
{
    // One shared API and database for all test classes, which therefore run one after another.
    public const string Name = "Api";
}

/// <summary>An API whose database cannot be reached, to test the 500 and health-check failure paths.</summary>
public class BrokenDatabaseFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:TechStore",
            "Host=localhost;Port=1;Database=nothing;Username=postgres;Timeout=2;Command Timeout=2");
    }
}
