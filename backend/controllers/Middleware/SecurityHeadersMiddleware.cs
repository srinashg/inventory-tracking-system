namespace TechStore.Api.Middleware;

/// <summary>
/// Adds protective response headers to every response, including errors: no sniffing of content types, no referrer
/// leakage, no framing. Responses under /api are also marked uncacheable, so a shared cache or the browser's back
/// button can never show someone's data after they sign out. (A full content-security policy for the web pages
/// belongs to whatever serves the React files: see docs/deployment-security-checklist.md.)
/// </summary>
public class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public Task InvokeAsync(HttpContext context)
    {
        // OnStarting runs just before the response is sent, so the headers are added however the response is produced
        // (a controller, the error middleware, the rate limiter, the authentication challenge).
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            headers["X-Frame-Options"] = "DENY";
            headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";

            if (context.Request.Path.StartsWithSegments("/api"))
            {
                headers["Cache-Control"] = "no-store";
                headers["Pragma"] = "no-cache";
            }

            return Task.CompletedTask;
        });

        return _next(context);
    }
}
