using System.Net;
using System.Net.Http.Json;
using TechStore.Api.Tests.Infrastructure;

namespace TechStore.Api.Tests;

/// <summary>
/// Settings that matter when the API runs in a container behind a web server (Step 11): the Secure cookie rule, the single
/// trusted proxy for client addresses, and the persistent Data Protection key ring.
/// </summary>
public class DeploymentConfigApiTests : ApiTestBase
{
    private static readonly Uri Https = new("https://localhost");

    public DeploymentConfigApiTests(ApiFactory factory) : base(factory)
    {
    }

    private ConfiguredApiFactory Host(string environment, params (string Key, string Value)[] settings)
    {
        var all = new Dictionary<string, string?>
        {
            ["Auth:RateLimit:LoginPerClientPerMinute"] = "100000",
            ["Auth:RateLimit:LoginGlobalPerMinute"] = "100000",
        };
        foreach (var (key, value) in settings) all[key] = value;
        return new ConfiguredApiFactory(Factory.ConnectionString, environment, all);
    }

    private async Task<HttpResponseMessage> SignInAsync(ConfiguredApiFactory host, Uri baseAddress)
    {
        var account = Factory.Account("Sales");
        return await host.Browser(baseAddress: baseAddress).PostAsJsonAsync("/api/auth/login", new { email = account.Email, password = account.Password }, Json);
    }

    private static string SessionCookie(HttpResponseMessage response) =>
        response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("techstore.session=", StringComparison.Ordinal));

    private static bool IsSecure(string setCookie) => setCookie.Contains("; secure", StringComparison.OrdinalIgnoreCase);

    // ---- Secure cookie rule ---------------------------------------------------------------------------------

    [Fact]
    public async Task InProduction_TheSessionCookieIsSecureByDefault()
    {
        await using var host = Host("Production");

        var response = await SignInAsync(host, Https);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(IsSecure(SessionCookie(response)));
    }

    [Fact]
    public void InProduction_TurningSecureCookiesOff_IsRefusedUnlessLocalHttpIsExplicitlyAllowed()
    {
        using var host = Host("Production", ("Auth:RequireSecureCookie", "false"));

        var failure = Assert.ThrowsAny<Exception>(() => host.Browser());

        Assert.Contains("Auth:RequireSecureCookie", failure.ToString());
        Assert.Contains("Auth:AllowInsecureCookiesForLocalHttp", failure.ToString());
    }

    [Fact]
    public async Task InProduction_ExplicitlyAllowedLocalHttp_GivesACookieWithoutTheSecureFlag()
    {
        await using var host = Host("Production", ("Auth:RequireSecureCookie", "false"), ("Auth:AllowInsecureCookiesForLocalHttp", "true"));

        var response = await SignInAsync(host, new Uri("http://localhost"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(IsSecure(SessionCookie(response)));
    }

    [Fact]
    public async Task TheLocalHttpAllowance_AloneDoesNotWeakenAnything()
    {
        await using var host = Host("Production", ("Auth:AllowInsecureCookiesForLocalHttp", "true"));

        var response = await SignInAsync(host, Https);

        Assert.True(IsSecure(SessionCookie(response)));
    }

    [Fact]
    public async Task InTheTestingEnvironment_TheCookieIsNotSecureByDefault_ButCanBeRequired()
    {
        await using var byDefault = Host("Testing");
        await using var required = Host("Testing", ("Auth:RequireSecureCookie", "true"));

        Assert.False(IsSecure(SessionCookie(await SignInAsync(byDefault, new Uri("http://localhost")))));
        Assert.True(IsSecure(SessionCookie(await SignInAsync(required, Https))));
    }

    // ---- One trusted proxy for client addresses -------------------------------------------------------------

    private static Task<HttpResponseMessage> Attempt(HttpClient client, string? remoteAddress, string? forwardedFor)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { email = "nobody@test.local", password = "wrong-password" }),
        };
        if (remoteAddress is not null) request.Headers.Add(ConfiguredApiFactory.RemoteAddressHeader, remoteAddress);
        if (forwardedFor is not null) request.Headers.Add("X-Forwarded-For", forwardedFor);
        return client.SendAsync(request);
    }

    private ConfiguredApiFactory LimitedHost(params (string Key, string Value)[] more) =>
        Host("Testing", new[] { ("Auth:RateLimit:LoginPerClientPerMinute", "3") }.Concat(more).ToArray());

    private static async Task UseUp(HttpClient client, string? remote, Func<int, string?> forwardedFor)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await Attempt(client, remote, forwardedFor(attempt))).StatusCode);
        }
    }

    [Fact]
    public async Task FromTheTrustedProxy_EachRealClientAddressHasItsOwnLimit()
    {
        await using var host = LimitedHost(("Auth:TrustedProxies:0", "10.0.0.9"));
        var client = host.Browser();
        await UseUp(client, "10.0.0.9", _ => "203.0.113.5");

        var sameClient = await Attempt(client, "10.0.0.9", "203.0.113.5");
        var otherClient = await Attempt(client, "10.0.0.9", "203.0.113.6");

        Assert.Equal(HttpStatusCode.TooManyRequests, sameClient.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, otherClient.StatusCode); // a different person is not punished
    }

    [Fact]
    public async Task FromAnyoneElse_AForgedForwardedForHeaderIsIgnored()
    {
        await using var host = LimitedHost(("Auth:TrustedProxies:0", "10.0.0.9"));
        var client = host.Browser();
        await UseUp(client, "10.0.0.77", attempt => $"203.0.113.{attempt}"); // not the proxy, pretending to be many clients

        var fourth = await Attempt(client, "10.0.0.77", "198.51.100.200");

        Assert.Equal(HttpStatusCode.TooManyRequests, fourth.StatusCode);
    }

    [Fact]
    public async Task WithoutATrustedProxySetting_ForwardedForIsIgnoredEvenFromAProxyLikeAddress()
    {
        await using var host = LimitedHost();
        var client = host.Browser();
        await UseUp(client, "10.0.0.9", attempt => $"203.0.113.{attempt}");

        var fourth = await Attempt(client, "10.0.0.9", "198.51.100.200");

        Assert.Equal(HttpStatusCode.TooManyRequests, fourth.StatusCode);
    }

    [Fact]
    public async Task ThroughTheTrustedProxy_OnlyTheLastAddressCounts_SoAClientCannotPrependAFakeOne()
    {
        await using var host = LimitedHost(("Auth:TrustedProxies:0", "10.0.0.9"));
        var client = host.Browser();
        // A client sent "X-Forwarded-For: <anything>" and a proxy that appends would add the real address at the end.
        await UseUp(client, "10.0.0.9", attempt => $"198.51.100.{attempt}, 203.0.113.5");

        var fourth = await Attempt(client, "10.0.0.9", "192.0.2.77, 203.0.113.5");

        Assert.Equal(HttpStatusCode.TooManyRequests, fourth.StatusCode);
    }

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("10.0.0.0/24")]
    public void ATrustedProxyThatIsNotASingleAddress_StopsTheApiFromStarting(string value)
    {
        using var host = Host("Testing", ("Auth:TrustedProxies:0", value));

        var failure = Assert.ThrowsAny<Exception>(() => host.Browser());

        Assert.Contains("Auth:TrustedProxies", failure.ToString());
    }

    // ---- Persistent Data Protection keys --------------------------------------------------------------------

    [Fact]
    public async Task WithAKeysPath_TheKeyRingIsWrittenThere_AndASessionSurvivesARestart()
    {
        var keys = Path.Combine(Path.GetTempPath(), "techstore-keys-" + Guid.NewGuid().ToString("N"));
        try
        {
            string cookie;
            await using (var first = Host("Testing", ("DataProtection:KeysPath", keys)))
            {
                var login = await SignInAsync(first, new Uri("http://localhost"));
                Assert.Equal(HttpStatusCode.OK, login.StatusCode);
                cookie = SessionCookie(login).Split(';')[0]; // "techstore.session=<protected value>"
            }

            Assert.NotEmpty(Directory.GetFiles(keys, "key-*.xml")); // the keys outlive the first API

            await using var second = Host("Testing", ("DataProtection:KeysPath", keys));
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
            request.Headers.Add("Cookie", cookie);
            var response = await second.CreateClient().SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode); // a freshly started API still accepts the old session
        }
        finally
        {
            if (Directory.Exists(keys)) Directory.Delete(keys, recursive: true);
        }
    }
}
