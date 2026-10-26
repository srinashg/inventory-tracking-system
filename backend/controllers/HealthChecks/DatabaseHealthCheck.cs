using Microsoft.Extensions.Diagnostics.HealthChecks;
using TechStore.Database;

namespace TechStore.Api.HealthChecks;

/// <summary>Reports healthy only when the API can reach PostgreSQL.</summary>
public class DatabaseHealthCheck : IHealthCheck
{
    private readonly TechStoreDbContext _db;

    public DatabaseHealthCheck(TechStoreDbContext db)
    {
        _db = db;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // CanConnectAsync opens a connection and runs a trivial query; it returns false instead of throwing.
        return await _db.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Cannot reach the database.");
    }
}
