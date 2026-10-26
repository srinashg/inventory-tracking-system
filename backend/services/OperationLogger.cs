using Microsoft.Extensions.Logging;
using TechStore.Services.Exceptions;

namespace TechStore.Services;

/// <summary>
/// Wraps a stock-changing operation to (1) write the audit log line required by NFR7 (operation name, outcome,
/// and the order or stock it concerned) and (2) translate PostgreSQL deadlock/serialization aborts into a
/// retryable <see cref="ConflictException"/>. Domain exceptions are logged as "rejected" and rethrown unchanged.
/// </summary>
internal static class OperationLogger
{
    public static async Task<T> RunAsync<T>(ILogger logger, string operation, string reference, Func<Task<T>> action)
    {
        try
        {
            T result;
            try
            {
                result = await action();
            }
            catch (Exception ex) when (ex.IsConcurrencyFailure())
            {
                // The transaction was rolled back by PostgreSQL; nothing changed. Report it as a retryable conflict.
                throw new ConflictException("The request collided with another update and was not applied. Please try again.");
            }

            logger.LogInformation("{Operation} succeeded ({Reference})", operation, reference);
            return result;
        }
        catch (Exception ex) when (ex is NotFoundException or ConflictException or ValidationFailedException)
        {
            // The request was refused and no data changed.
            logger.LogWarning("{Operation} rejected ({Reference}): {Reason}", operation, reference, ex.Message);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Operation} failed ({Reference})", operation, reference);
            throw;
        }
    }
}
