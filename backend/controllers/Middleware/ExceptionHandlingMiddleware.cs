using Microsoft.AspNetCore.Mvc;
using TechStore.Auth;
using TechStore.Services.Exceptions;

namespace TechStore.Api.Middleware;

/// <summary>
/// Turns the exceptions thrown by services into the error responses promised in docs/api-contract.md, so
/// controllers never need try/catch. Every error is a ProblemDetails JSON body (application/problem+json).
/// </summary>
public class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (ValidationFailedException ex)
        {
            await WriteAsync(context, new ValidationProblemDetails(ex.Errors.ToDictionary(e => e.Key, e => e.Value))
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Validation failed",
                Detail = ex.Message
            });
        }
        catch (InvalidCredentialsException ex)
        {
            // Wrong password, unknown e-mail and locked account all look the same on purpose.
            await WriteAsync(context, Problem(StatusCodes.Status401Unauthorized, "Unauthorized", ex.Message));
        }
        catch (NotSignedInException ex)
        {
            await WriteAsync(context, Problem(StatusCodes.Status401Unauthorized, "Unauthorized", ex.Message));
        }
        catch (NotFoundException ex)
        {
            await WriteAsync(context, Problem(StatusCodes.Status404NotFound, "Not Found", ex.Message));
        }
        catch (ConflictException ex)
        {
            await WriteAsync(context, Problem(StatusCodes.Status409Conflict, "Conflict", ex.Message));
        }
        catch (Exception ex)
        {
            // Unexpected: the full exception goes to the log only. The client gets a generic message.
            _logger.LogError(ex, "Unhandled exception for {Method} {Path}", context.Request.Method, context.Request.Path);
            await WriteAsync(context, Problem(
                StatusCodes.Status500InternalServerError, "Internal Server Error", "An unexpected error occurred."));
        }
    }

    private static ProblemDetails Problem(int status, string title, string detail) =>
        new() { Status = status, Title = title, Detail = detail };

    private static async Task WriteAsync(HttpContext context, ProblemDetails problem)
    {
        if (context.Response.HasStarted)
        {
            return; // too late to change the response
        }

        context.Response.Clear();
        context.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(problem, problem.GetType(), options: null, contentType: "application/problem+json");
    }
}
