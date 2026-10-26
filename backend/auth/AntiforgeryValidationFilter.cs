using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace TechStore.Auth;

/// <summary>
/// CSRF protection for the cookie session: every request that is not a plain read (POST, PATCH, DELETE, ...) must
/// carry the X-CSRF-TOKEN header from GET /api/auth/csrf. A missing or wrong token is a 400 problem+json response.
/// It runs after the authentication/permission check, so an anonymous caller still gets 401 and a caller without
/// permission still gets 403 rather than a token complaint.
/// </summary>
public class AntiforgeryValidationFilter : IAsyncAuthorizationFilter, IOrderedFilter
{
    public const string HeaderName = "X-CSRF-TOKEN";

    // Later than the built-in permission check, which runs at order 0.
    public int Order => 1000;

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (HttpMethods.IsGet(context.HttpContext.Request.Method)
            || HttpMethods.IsHead(context.HttpContext.Request.Method)
            || HttpMethods.IsOptions(context.HttpContext.Request.Method)
            || HttpMethods.IsTrace(context.HttpContext.Request.Method))
        {
            return;
        }

        var antiforgery = context.HttpContext.RequestServices.GetRequiredService<IAntiforgery>();
        try
        {
            await antiforgery.ValidateRequestAsync(context.HttpContext);
        }
        catch (AntiforgeryValidationException)
        {
            context.Result = new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Bad Request",
                Detail = "The anti-forgery token is missing or invalid. Reload the page and try again."
            })
            {
                StatusCode = StatusCodes.Status400BadRequest,
                ContentTypes = { "application/problem+json" }
            };
        }
    }
}
