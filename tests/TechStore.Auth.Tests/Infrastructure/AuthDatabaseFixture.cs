using Microsoft.EntityFrameworkCore;
using Npgsql;
using TechStore.Database;

namespace TechStore.Auth.Tests.Infrastructure;

/// <summary>
/// Creates the techstore_auth_test database (if missing) and applies every EF migration once per run.
/// A separate database keeps these tests away from development data and from the other test projects.
/// </summary>
public class AuthDatabaseFixture : IAsyncLifetime
{
    private const string DefaultConnection =
        "Host=localhost;Port=5432;Database=techstore_auth_test;Username=postgres;Maximum Pool Size=30";

    public string ConnectionString { get; } =
        Environment.GetEnvironmentVariable("TECHSTORE_AUTH_TEST_DB") ?? DefaultConnection;

    public async Task InitializeAsync()
    {
        var databaseName = new NpgsqlConnectionStringBuilder(ConnectionString).Database!;
        var admin = new NpgsqlConnectionStringBuilder(ConnectionString) { Database = "postgres", Pooling = false };

        await using (var connection = new NpgsqlConnection(admin.ConnectionString))
        {
            await connection.OpenAsync();
            await using var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", connection);
            exists.Parameters.AddWithValue("name", databaseName);
            if (await exists.ExecuteScalarAsync() is null)
            {
                await using var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", connection);
                await create.ExecuteNonQueryAsync();
            }
        }

        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public TechStoreDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<TechStoreDbContext>().UseNpgsql(ConnectionString).Options);
}

[CollectionDefinition(Name)]
public class AuthDatabaseCollection : ICollectionFixture<AuthDatabaseFixture>
{
    // One shared database, so test classes run one after another.
    public const string Name = "AuthDatabase";
}
