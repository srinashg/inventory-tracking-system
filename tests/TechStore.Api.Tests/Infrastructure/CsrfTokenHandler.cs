using System.Net.Http.Json;

namespace TechStore.Api.Tests.Infrastructure;

/// <summary>
/// Does what the React app will do: fetches an anti-forgery token from GET /api/auth/csrf and sends it in the
/// X-CSRF-TOKEN header on every POST/PATCH/DELETE. The token belongs to one identity, so it is fetched again after
/// login and logout.
/// </summary>
public sealed class CsrfTokenHandler : DelegatingHandler
{
    private string? _token;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var changesData = request.Method != HttpMethod.Get && request.Method != HttpMethod.Head
            && request.Method != HttpMethod.Options && request.Method != HttpMethod.Trace;

        if (changesData && !request.Headers.Contains("X-CSRF-TOKEN"))
        {
            _token ??= await FetchTokenAsync(request.RequestUri!, cancellationToken);
            request.Headers.Add("X-CSRF-TOKEN", _token);
        }

        var response = await base.SendAsync(request, cancellationToken);

        var path = request.RequestUri!.AbsolutePath;
        if (path is "/api/auth/login" or "/api/auth/logout")
        {
            _token = null; // the identity changed, so the old token no longer fits
        }

        return response;
    }

    private async Task<string> FetchTokenAsync(Uri requestUri, CancellationToken cancellationToken)
    {
        var tokenRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(requestUri, "/api/auth/csrf"));
        var response = await base.SendAsync(tokenRequest, cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<TokenBody>(cancellationToken: cancellationToken);
        return body!.Token;
    }

    private sealed record TokenBody(string Token);
}
