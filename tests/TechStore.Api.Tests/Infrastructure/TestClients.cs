using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TechStore.Api.Tests.Infrastructure;

/// <summary>
/// A tiny cookie jar for test browsers. (WebApplicationFactory only gives a client a cookie jar when it has no custom
/// handlers, and these tests need both.) Like a real browser, it does not return a Secure cookie over plain http.
/// </summary>
public sealed class CookieHandler : DelegatingHandler
{
    private readonly CookieContainer _cookies = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var cookieHeader = _cookies.GetCookieHeader(uri);
        if (!string.IsNullOrEmpty(cookieHeader))
        {
            request.Headers.Remove("Cookie");
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        }

        var response = await base.SendAsync(request, cancellationToken);

        if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            foreach (var setCookie in setCookies) _cookies.SetCookies(uri, setCookie);
        }

        return response;
    }
}

public static class TestClients
{
    /// <summary>A browser with cookies. With <paramref name="addCsrfToken"/> it also sends X-CSRF-TOKEN on POST/PATCH like the React app will.</summary>
    public static HttpClient Browser<T>(this WebApplicationFactory<T> factory, bool addCsrfToken = true, Uri? baseAddress = null) where T : class
    {
        var handlers = addCsrfToken
            ? new DelegatingHandler[] { new CsrfTokenHandler(), new CookieHandler() }
            : new DelegatingHandler[] { new CookieHandler() };

        return factory.CreateDefaultClient(baseAddress ?? new Uri("http://localhost"), handlers);
    }
}
