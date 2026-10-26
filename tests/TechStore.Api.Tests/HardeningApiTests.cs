using System.Net;
using System.Net.Http.Json;
using TechStore.Api.Tests.Infrastructure;

namespace TechStore.Api.Tests;

/// <summary>Login rate limiting and the cache and security response headers (Step 10F).</summary>
public class HardeningApiTests : ApiTestBase
{
    public HardeningApiTests(ApiFactory factory) : base(factory)
    {
    }

    private HttpClient Browser(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory) => factory.Browser();

    private static Task<HttpResponseMessage> Attempt(HttpClient client, string email, string password, string? forwardedFor = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login") { Content = JsonContent.Create(new { email, password }) };
        if (forwardedFor is not null) request.Headers.Add("X-Forwarded-For", forwardedFor);
        return client.SendAsync(request);
    }

    // ---- Login rate limiting ------------------------------------------------------------------------------

    [Fact]
    public async Task TheEleventhLoginAttemptInAMinute_FromOneClient_Gets429()
    {
        await using var limited = Factory.WithWebHostBuilder(b => b.UseSetting("Auth:RateLimit:LoginPerClientPerMinute", "10"));
        var client = Browser(limited);

        for (var attempt = 1; attempt <= 10; attempt++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await Attempt(client, "nobody@test.local", "wrong-password-" + attempt)).StatusCode);
        }

        var blocked = await Attempt(client, "nobody@test.local", "wrong-password-11");

        var problem = await ExpectProblemAsync(blocked, HttpStatusCode.TooManyRequests);
        Assert.Contains("Too many sign-in attempts", problem.Detail);
        Assert.True(blocked.Headers.RetryAfter?.Delta > TimeSpan.Zero);
    }

    [Fact]
    public async Task WhileRateLimited_EvenTheCorrectPasswordIsRefused()
    {
        await using var limited = Factory.WithWebHostBuilder(b => b.UseSetting("Auth:RateLimit:LoginPerClientPerMinute", "3"));
        var client = Browser(limited);
        var account = Factory.Account("Sales");
        for (var attempt = 1; attempt <= 3; attempt++) await Attempt(client, "nobody@test.local", "wrong-password-" + attempt);

        var response = await Attempt(client, account.Email, account.Password);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie") && response.Headers.GetValues("Set-Cookie").Any(c => c.StartsWith("techstore.session=")));
    }

    [Fact]
    public async Task SuccessfulLogins_CountTowardTheLimitToo()
    {
        await using var limited = Factory.WithWebHostBuilder(b => b.UseSetting("Auth:RateLimit:LoginPerClientPerMinute", "2"));
        var account = Factory.Account("Sales");

        var first = await Attempt(Browser(limited), account.Email, account.Password);
        var second = await Attempt(Browser(limited), account.Email, account.Password);
        var third = await Attempt(Browser(limited), account.Email, account.Password);

        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests }, new[] { first.StatusCode, second.StatusCode, third.StatusCode });
    }

    [Fact]
    public async Task TheLimit_AppliesOnlyToLogin_NotToOtherEndpoints()
    {
        await using var limited = Factory.WithWebHostBuilder(b => b.UseSetting("Auth:RateLimit:LoginPerClientPerMinute", "1"));
        var client = Browser(limited);
        var account = Factory.Account("Admin");
        Assert.Equal(HttpStatusCode.OK, (await Attempt(client, account.Email, account.Password)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Attempt(client, account.Email, account.Password)).StatusCode);

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/products")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/csrf")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task AForgedForwardedForHeader_CannotBeUsedToDodgeTheLimit()
    {
        await using var limited = Factory.WithWebHostBuilder(b => b.UseSetting("Auth:RateLimit:LoginPerClientPerMinute", "3"));
        var client = Browser(limited);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await Attempt(client, "nobody@test.local", "wrong-password-" + attempt, forwardedFor: $"203.0.113.{attempt}");
        }

        var fourth = await Attempt(client, "nobody@test.local", "wrong-password-4", forwardedFor: "198.51.100.77");

        Assert.Equal(HttpStatusCode.TooManyRequests, fourth.StatusCode); // the header is ignored; the connection's address counts
    }

    [Fact]
    public async Task TheGlobalCap_StopsLoginFloodsEvenWhenEachClientIsUnderItsOwnLimit()
    {
        await using var limited = Factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Auth:RateLimit:LoginPerClientPerMinute", "100");
            b.UseSetting("Auth:RateLimit:LoginGlobalPerMinute", "5");
        });
        var client = Browser(limited);

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await Attempt(client, "nobody@test.local", "wrong-password-" + attempt)).StatusCode);
        }

        await ExpectProblemAsync(await Attempt(client, "nobody@test.local", "wrong-password-6"), HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public void TheDefaults_AreTenPerClientAndOneHundredGlobalPerMinute()
    {
        var options = new TechStore.Auth.AuthOptions();

        Assert.Equal(10, options.RateLimit.LoginPerClientPerMinute);
        Assert.Equal(100, options.RateLimit.LoginGlobalPerMinute);
    }

    [Theory]
    [InlineData("Auth:RateLimit:LoginPerClientPerMinute", "0")]
    [InlineData("Auth:RateLimit:LoginGlobalPerMinute", "-1")]
    public void AnInvalidRateLimit_StopsTheApiFromStarting(string key, string value)
    {
        using var broken = Factory.WithWebHostBuilder(b => b.UseSetting(key, value));

        Assert.ThrowsAny<Exception>(() => broken.Browser());
    }

    // ---- Response headers ---------------------------------------------------------------------------------

    private static void AssertSecurityHeaders(HttpResponseMessage response)
    {
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    private static void AssertNotCacheable(HttpResponseMessage response)
    {
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        Assert.Contains("no-cache", response.Headers.GetValues("Pragma"));
    }

    [Fact]
    public async Task ApiResponses_AreNotCacheable_AndCarrySecurityHeaders()
    {
        var response = await Client.GetAsync("/api/products");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertNotCacheable(response);
        AssertSecurityHeaders(response);
    }

    [Fact]
    public async Task ErrorResponses_AreAlsoNotCacheable_AndCarrySecurityHeaders()
    {
        foreach (var response in new[]
        {
            await AnonymousClient().GetAsync("/api/products"),            // 401
            await Client.GetAsync("/api/orders/999"),                      // 404
            await (await SignedInClientAsync("Sales")).PostAsJsonAsync("/api/products", new { }, Json), // 403
        })
        {
            AssertNotCacheable(response);
            AssertSecurityHeaders(response);
        }
    }

    [Fact]
    public async Task TheLoginAndLogoutResponses_AreNotCacheable()
    {
        var account = Factory.Account("Warehouse");
        var client = AnonymousClient();

        var login = await client.PostAsJsonAsync("/api/auth/login", new { email = account.Email, password = account.Password }, Json);
        var logout = await client.PostAsync("/api/auth/logout", null);

        AssertNotCacheable(login);
        AssertNotCacheable(logout);
    }

    [Fact]
    public async Task TheCsrfTokenResponse_IsNotCacheable()
    {
        AssertNotCacheable(await AnonymousClient().GetAsync("/api/auth/csrf"));
    }

    [Fact]
    public async Task TheHealthEndpoint_CarriesTheSecurityHeaders()
    {
        var response = await AnonymousClient().GetAsync("/health");

        AssertSecurityHeaders(response);
    }

    [Fact]
    public async Task RateLimitedResponses_CarryTheHeadersToo()
    {
        await using var limited = Factory.WithWebHostBuilder(b => b.UseSetting("Auth:RateLimit:LoginPerClientPerMinute", "1"));
        var client = Browser(limited);
        await Attempt(client, "nobody@test.local", "wrong-password-1");

        var blocked = await Attempt(client, "nobody@test.local", "wrong-password-2");

        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        AssertNotCacheable(blocked);
        AssertSecurityHeaders(blocked);
    }
}
