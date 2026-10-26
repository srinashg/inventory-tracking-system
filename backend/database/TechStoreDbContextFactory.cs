using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TechStore.Database;

/// <summary>
/// Used only by the EF Core command-line tools (dotnet ef migrations / database update) to build a
/// DbContext at design time. Set TECHSTORE_DB to use another database; the default is the local
/// development container, which accepts local connections without a password.
/// </summary>
public class TechStoreDbContextFactory : IDesignTimeDbContextFactory<TechStoreDbContext>
{
    private const string LocalDevConnection = "Host=localhost;Port=5432;Database=techstore;Username=postgres";

    public TechStoreDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("TECHSTORE_DB") ?? LocalDevConnection;

        var options = new DbContextOptionsBuilder<TechStoreDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new TechStoreDbContext(options);
    }
}
