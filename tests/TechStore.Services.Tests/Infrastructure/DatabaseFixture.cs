using Microsoft.EntityFrameworkCore;
using Npgsql;
using TechStore.Database;

namespace TechStore.Services.Tests.Infrastructure;

/// <summary>
/// Creates the techstore_test database (if missing) and applies the EF migrations once per test run.
/// The tests use real PostgreSQL because the services rely on conditional UPDATEs, row locks,
/// CHECK constraints and transactions, which the EF in-memory provider does not implement.
/// </summary>
public class DatabaseFixture : IAsyncLifetime
{
    private const string DefaultConnection =
        "Host=localhost;Port=5432;Database=techstore_test;Username=postgres;Maximum Pool Size=50";

    public string ConnectionString { get; } =
        Environment.GetEnvironmentVariable("TECHSTORE_TEST_DB") ?? DefaultConnection;

    public async Task InitializeAsync()
    {
        await CreateDatabaseIfMissingAsync();

        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>A new DbContext, like the one each web request gets in production.</summary>
    public TechStoreDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TechStoreDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;

        return new TechStoreDbContext(options);
    }

    private async Task CreateDatabaseIfMissingAsync()
    {
        var target = new NpgsqlConnectionStringBuilder(ConnectionString);
        var databaseName = target.Database!;

        // Connect to the built-in "postgres" database to be able to create ours.
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
public class DatabaseCollection : ICollectionFixture<DatabaseFixture>
{
    // All test classes share one database, so they run one after another (never in parallel with each other).
    public const string Name = "Database";
}
