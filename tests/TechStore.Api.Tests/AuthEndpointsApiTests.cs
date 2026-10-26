using System.Net;
using System.Net.Http.Json;
using TechStore.Api.Tests.Infrastructure;
using TechStore.Dto;

namespace TechStore.Api.Tests;

/// <summary>Login, logout, "who am I" and the CSRF token (docs/api-contract.md section 10).</summary>
public class AuthEndpointsApiTests : ApiTestBase
{
    public AuthEndpointsApiTests(ApiFactory factory) : base(factory)
    {
    }

    private async Task<HttpResponseMessage> LoginAsync(HttpClient client, string email, string password) =>
        await client.PostAsJsonAsync("/api/auth/login", new { email, password }, Json);

    private static string[] SetCookies(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToArray() : Array.Empty<string>();

    private static string? SessionCookie(HttpResponseMessage response) =>
        SetCookies(response).FirstOrDefault(c => c.StartsWith("techstore.session=", StringComparison.Ordinal));

    // ---- GET /api/auth/csrf ------------------------------------------------------------------------------

    [Fact]
    public async Task Csrf_ReturnsATokenToAnAnonymousVisitor_AndSetsAnHttpOnlyCsrfCookie()
    {
        var client = RawClient();

        var response = await client.GetAsync("/api/auth/csrf");

        var body = await ExpectAsync<CsrfTokenResponse>(response, HttpStatusCode.OK);
        Assert.False(string.IsNullOrWhiteSpace(body.Token));
        var cookie = Assert.Single(SetCookies(response), c => c.StartsWith("techstore.csrf=", StringComparison.Ordinal)).ToLowerInvariant();
        Assert.Contains("httponly", cookie);
        Assert.Contains("samesite=strict", cookie);
    }

    // ---- POST /api/auth/login ----------------------------------------------------------------------------

    [Theory]
    [InlineData("Admin", new[] { "ManageProducts", "ManageStock", "ManageOrders", "FulfillOrders" })]
    [InlineData("Sales", new[] { "ManageOrders" })]
    [InlineData("Warehouse", new[] { "ManageStock", "FulfillOrders" })]
    public async Task Login_WithValidCredentials_Returns200WithRolesAndPermissions(string role, string[] permissions)
    {
        var account = Factory.Account(role);

        var response = await LoginAsync(AnonymousClient(), account.Email, account.Password);

        var user = await ExpectAsync<CurrentUserResponse>(response, HttpStatusCode.OK);
        Assert.Equal(account.Email, user.Email);
        Assert.Equal(new[] { role }, user.Roles);
        Assert.Equal(permissions, user.Permissions);
        Assert.True(user.UserId > 0);
    }

    [Fact]
    public async Task Login_IgnoresTheCaseOfTheEmail()
    {
        var account = Factory.Account("Sales");

        var response = await LoginAsync(AnonymousClient(), account.Email.ToUpperInvariant(), account.Password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Login_SetsAnHttpOnlySameSiteStrictSessionCookie_ThatNeverContainsThePassword()
    {
        var account = Factory.Account("Admin");

        var response = await LoginAsync(AnonymousClient(), account.Email, account.Password);

        var cookie = SessionCookie(response);
        Assert.NotNull(cookie);
        var lower = cookie.ToLowerInvariant();
        Assert.Contains("httponly", lower);
        Assert.Contains("samesite=strict", lower);
        Assert.Contains("path=/", lower);
        Assert.DoesNotContain("secure", lower.Replace("samesite", "")); // plain http is allowed only in the Testing environment
        Assert.DoesNotContain(account.Password, cookie);
        Assert.DoesNotContain(account.Password, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Login_ResponseNeverIncludesAPasswordHashOrSecurityStamp()
    {
        var account = Factory.Account("Admin");

        var body = await (await LoginAsync(AnonymousClient(), account.Email, account.Password)).Content.ReadAsStringAsync();

        Assert.DoesNotContain("hash", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stamp", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Login_WithInvalidCredentials_Returns401WithTheSameBodyEveryTime_AndNoSessionCookie()
    {
        var account = Factory.Account("Sales");
        var client = AnonymousClient();

        var wrongPassword = await LoginAsync(client, account.Email, account.Password + "x");
        var unknownEmail = await LoginAsync(client, "nobody@test.local", account.Password);

        foreach (var response in new[] { wrongPassword, unknownEmail })
        {
            Assert.Null(SessionCookie(response));
        }

        var first = await ExpectProblemAsync(wrongPassword, HttpStatusCode.Unauthorized);
        var second = await ExpectProblemAsync(unknownEmail, HttpStatusCode.Unauthorized);
        Assert.Equal("Invalid email or password.", first.Detail);
        Assert.Equal(first, second); // identical, so the response reveals nothing about which accounts exist
    }

    [Fact]
    public async Task Login_AfterFiveWrongPasswords_TheAccountIsLocked_AndEvenTheCorrectPasswordGetsTheSame401()
    {
        var account = Factory.Account("Warehouse");
        var client = AnonymousClient();

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var failed = await ExpectProblemAsync(await LoginAsync(client, account.Email, "wrong-password-" + attempt), HttpStatusCode.Unauthorized);
            Assert.Equal("Invalid email or password.", failed.Detail);
        }

        var locked = await LoginAsync(client, account.Email, account.Password);
        var problem = await ExpectProblemAsync(locked, HttpStatusCode.Unauthorized);
        Assert.Equal("Invalid email or password.", problem.Detail);
        Assert.Null(SessionCookie(locked));
    }

    [Fact]
    public async Task Login_ASuccessfulLoginResetsTheFailureCount()
    {
        var account = Factory.Account("Sales");
        var client = AnonymousClient();
        for (var attempt = 1; attempt <= 4; attempt++) await LoginAsync(client, account.Email, "wrong-" + attempt);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, account.Email, account.Password)).StatusCode);

        for (var attempt = 1; attempt <= 4; attempt++) await LoginAsync(AnonymousClient(), account.Email, "wrong-again-" + attempt);

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(AnonymousClient(), account.Email, account.Password)).StatusCode);
    }

    [Theory]
    [InlineData("", "some-password-value")]
    [InlineData("sales@test.local", "")]
    [InlineData("", "")]
    public async Task Login_WithBlankFields_Returns400WithFieldErrors(string email, string password)
    {
        var problem = await ExpectProblemAsync(await LoginAsync(AnonymousClient(), email, password), HttpStatusCode.BadRequest);

        Assert.NotNull(problem.Errors);
        Assert.NotEmpty(problem.Errors);
    }

    [Fact]
    public async Task Login_WithNoBody_Returns400()
    {
        var client = AnonymousClient();

        var response = await client.PostAsync("/api/auth/login", new StringContent("", System.Text.Encoding.UTF8, "application/json"));

        await ExpectProblemAsync(response, HttpStatusCode.BadRequest);
    }

    // ---- CSRF protection ---------------------------------------------------------------------------------

    [Fact]
    public async Task Login_WithoutACsrfToken_Returns400()
    {
        var account = Factory.Account("Admin");

        var response = await RawClient().PostAsJsonAsync("/api/auth/login", new { email = account.Email, password = account.Password }, Json);

        var problem = await ExpectProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.Contains("anti-forgery", problem.Detail);
        Assert.Null(SessionCookie(response));
    }

    [Fact]
    public async Task Login_WithAWrongCsrfToken_Returns400()
    {
        var account = Factory.Account("Admin");
        var client = RawClient();
        await client.GetAsync("/api/auth/csrf"); // gets the matching cookie, but we send a different token
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { email = account.Email, password = account.Password }),
            Headers = { { "X-CSRF-TOKEN", "not-a-real-token" } },
        };

        await ExpectProblemAsync(await client.SendAsync(request), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ATokenFetchedBeforeLogin_IsRejectedAfterLogin_SoTheAppMustFetchANewOne()
    {
        var account = Factory.Account("Admin");
        var client = RawClient();
        var before = (await ExpectAsync<CsrfTokenResponse>(await client.GetAsync("/api/auth/csrf"), HttpStatusCode.OK)).Token;
        var login = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { email = account.Email, password = account.Password }),
            Headers = { { "X-CSRF-TOKEN", before } },
        };
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(login)).StatusCode);

        var withOldToken = new HttpRequestMessage(HttpMethod.Post, "/api/products")
        {
            Content = JsonContent.Create(new { sku = "A-1", name = "Thing", price = 1 }),
            Headers = { { "X-CSRF-TOKEN", before } },
        };
        await ExpectProblemAsync(await client.SendAsync(withOldToken), HttpStatusCode.BadRequest);

        var after = (await ExpectAsync<CsrfTokenResponse>(await client.GetAsync("/api/auth/csrf"), HttpStatusCode.OK)).Token;
        var withNewToken = new HttpRequestMessage(HttpMethod.Post, "/api/products")
        {
            Content = JsonContent.Create(new { sku = "A-1", name = "Thing", price = 1 }),
            Headers = { { "X-CSRF-TOKEN", after } },
        };
        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(withNewToken)).StatusCode);
    }

    [Fact]
    public async Task ASignedInUser_WithoutATokenOnAPost_GetsA400_AndNothingIsChanged()
    {
        var bare = await SignedInBareClientAsync("Admin");

        var response = await bare.PostAsJsonAsync("/api/products", new { sku = "A-1", name = "Thing", price = 1 }, Json);

        await ExpectProblemAsync(response, HttpStatusCode.BadRequest);
        var products = await ExpectAsync<PagedResponse<ProductResponse>>(await Client.GetAsync("/api/products"), HttpStatusCode.OK);
        Assert.Empty(products.Items);
    }

    [Fact]
    public async Task GetRequests_NeedNoCsrfToken()
    {
        var bare = await SignedInBareClientAsync("Sales");

        Assert.Equal(HttpStatusCode.OK, (await bare.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await bare.GetAsync("/api/products")).StatusCode);
    }

    /// <summary>A browser that signed in properly but never adds a CSRF header on its own afterwards.</summary>
    private async Task<HttpClient> SignedInBareClientAsync(string role)
    {
        var account = Factory.Account(role);
        var bare = RawClient();
        var csrf = (await ExpectAsync<CsrfTokenResponse>(await bare.GetAsync("/api/auth/csrf"), HttpStatusCode.OK)).Token;
        var login = await bare.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { email = account.Email, password = account.Password }),
            Headers = { { "X-CSRF-TOKEN", csrf } },
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return bare;
    }

    // ---- GET /api/auth/me --------------------------------------------------------------------------------

    [Fact]
    public async Task Me_WhenSignedIn_ReturnsTheUser()
    {
        var client = await SignedInClientAsync("Warehouse");

        var user = await ExpectAsync<CurrentUserResponse>(await client.GetAsync("/api/auth/me"), HttpStatusCode.OK);

        Assert.Equal("warehouse@test.local", user.Email);
        Assert.Equal(new[] { "Warehouse" }, user.Roles);
        Assert.Equal(new[] { "ManageStock", "FulfillOrders" }, user.Permissions);
    }

    [Fact]
    public async Task Me_WhenNotSignedIn_Returns401AsJson_WithNoRedirect()
    {
        var response = await AnonymousClient().GetAsync("/api/auth/me");

        var problem = await ExpectProblemAsync(response, HttpStatusCode.Unauthorized);
        Assert.Equal("Sign in to continue.", problem.Detail);
        Assert.Null(response.Headers.Location);
    }

    // ---- POST /api/auth/logout ---------------------------------------------------------------------------

    [Fact]
    public async Task Logout_Returns204_ClearsTheSession_AndLaterRequestsAreRefused()
    {
        var client = await SignedInClientAsync("Sales");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);

        var logout = await client.PostAsync("/api/auth/logout", null);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Contains(SetCookies(logout), c => c.StartsWith("techstore.session=", StringComparison.Ordinal) && c.Contains("expires=", StringComparison.OrdinalIgnoreCase));
        await ExpectProblemAsync(await client.GetAsync("/api/auth/me"), HttpStatusCode.Unauthorized);
        await ExpectProblemAsync(await client.GetAsync("/api/products"), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logout_ThenLoggingInAgain_RestoresAccess()
    {
        var client = await SignedInClientAsync("Sales");
        await client.PostAsync("/api/auth/logout", null);
        var account = Factory.Account("Sales");

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, account.Email, account.Password)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/products")).StatusCode);
    }

    [Fact]
    public async Task Logout_WithoutASession_Returns401()
    {
        await ExpectProblemAsync(await AnonymousClient().PostAsync("/api/auth/logout", null), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logout_WithoutACsrfToken_Returns400_AndTheSessionSurvives()
    {
        var bare = await SignedInBareClientAsync("Sales");

        await ExpectProblemAsync(await bare.PostAsync("/api/auth/logout", null), HttpStatusCode.BadRequest);

        Assert.Equal(HttpStatusCode.OK, (await bare.GetAsync("/api/auth/me")).StatusCode);
    }

    // ---- No cross-origin access --------------------------------------------------------------------------

    [Fact]
    public async Task TheApiDoesNotEnableCors_SoAnotherSiteCannotReadResponses()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/csrf");
        request.Headers.Add("Origin", "https://evil.example");

        var response = await RawClient().SendAsync(request);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }
}
