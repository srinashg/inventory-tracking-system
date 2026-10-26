using Npgsql;

namespace TechStore.Services;

/// <summary>Recognizes PostgreSQL error codes (SQLSTATE) in exceptions thrown by EF Core.</summary>
internal static class DatabaseErrors
{
    /// <summary>
    /// True when the exception, or any exception nested inside it, carries the given SQLSTATE. EF Core and Npgsql
    /// can wrap the PostgreSQL error several levels deep (for example InvalidOperationException → DbUpdateException → PostgresException).
    /// </summary>
    public static bool HasSqlState(this Exception exception, string sqlState)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres && postgres.SqlState == sqlState)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>PostgreSQL gave up on this transaction because of a deadlock or serialization failure; retrying can succeed.</summary>
    public static bool IsConcurrencyFailure(this Exception exception) =>
        exception.HasSqlState(PostgresErrorCodes.DeadlockDetected)
        || exception.HasSqlState(PostgresErrorCodes.SerializationFailure);

    /// <summary>A UNIQUE or PRIMARY KEY constraint was violated (23505).</summary>
    public static bool IsUniqueViolation(this Exception exception) =>
        exception.HasSqlState(PostgresErrorCodes.UniqueViolation);
}
