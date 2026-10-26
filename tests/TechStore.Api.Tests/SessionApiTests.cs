using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TechStore.Api.Tests.Infrastructure;
using TechStore.Auth;
using TechStore.Database.Entities;
using TechStore.Dto;

namespace TechStore.Api.Tests;

/// <summary>
/// When an existing session stops working (docs/api-contract.md section 10.6): password, role and lock changes rotate the
/// security stamp and end sessions; the check is configurable; sessions expire.
/// </summary>
public class SessionApiTests : ApiTestBase
{
    public SessionApiTests(ApiFactory factory) : base(factory)
    {
    }

    private const string NewPassword = "A-different-long-passphrase-42";

    /// <summary>Runs code as the application would: with the user manager and account service from a fresh scope.</summary>
    private async Task AsAdministratorAsync(Func<IServiceProvider, Task> action, WebApplicationFactory<Program>? factory = null)
    {
        await using var scope = (factory ?? Factory).Services.CreateAsyncScope();
        await action(scope.ServiceProvider);
    }

    private async Task<long> UserIdAsync(string email)
    {
        long id = 0;
        await AsAdministratorAsync(async sp => id = (await sp.GetRequiredService<UserManager<AppUser>>().FindByEmailAsync(email))!.Id);
        return id;
    }

    private async Task LoginAsync(HttpClient client, TestAccount account, string? password = null) =>
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login", new { email = account.Email, password = password ?? account.Password }, Json)).StatusCode);

    // ---- Sessions that must end ---------------------------------------------------------------------------

    [Fact]
    public async Task AValidSession_KeepsWorkingAcrossRequests()
    {
        var client = await SignedInClientAsync("Sales");

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
        }
    }

    [Fact]
    public async Task ChangingAPassword_EndsThatUsersSessions_AndTheOldPasswordStopsWorking()
    {
        var account = Factory.Account("Sales");
        var client = await SignedInClientAsync("Sales");
        var otherUser = await SignedInClientAsync("Warehouse");

        await AsAdministratorAsync(async sp =>
        {
            var users = sp.GetRequiredService<UserManager<AppUser>>();
            var user = (await users.FindByEmailAsync(account.Email))!;
            Assert.True((await users.ChangePasswordAsync(user, account.Password, NewPassword)).Succeeded);
        });

        await ExpectProblemAsync(await client.GetAsync("/api/auth/me"), HttpStatusCode.Unauthorized);
        Assert.Equal(HttpStatusCode.OK, (await otherUser.GetAsync("/api/auth/me")).StatusCode); // someone else is unaffected
        var fresh = AnonymousClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await fresh.PostAsJsonAsync("/api/auth/login", new { email = account.Email, password = account.Password }, Json)).StatusCode);
        await LoginAsync(fresh, account, NewPassword);
        Assert.Equal(HttpStatusCode.OK, (await fresh.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task AnAdministratorChangingARole_EndsTheUsersSessions_AndTheNextLoginHasTheNewRole()
    {
        var account = Factory.Account("Sales");
        var client = await SignedInClientAsync("Sales");
        var id = await UserIdAsync(account.Email);

        await AsAdministratorAsync(sp => sp.GetRequiredService<AccountService>().AssignRoleAsync(id, "Warehouse"));

        await ExpectProblemAsync(await client.GetAsync("/api/auth/me"), HttpStatusCode.Unauthorized);
        await LoginAsync(client, account);
        var me = await ExpectAsync<CurrentUserResponse>(await client.GetAsync("/api/auth/me"), HttpStatusCode.OK);
        Assert.Equal(new[] { "Warehouse" }, me.Roles);
        Assert.Equal(new[] { "ManageStock", "FulfillOrders" }, me.Permissions);
    }

    [Fact]
    public async Task LockingAnAccount_EndsItsSessions_AndBlocksLogin_UntilItIsUnlocked()
    {
        var account = Factory.Account("Sales");
        var client = await SignedInClientAsync("Sales");
        var id = await UserIdAsync(account.Email);

        await AsAdministratorAsync(sp => sp.GetRequiredService<AccountService>().SetLockedAsync(id, true));

        await ExpectProblemAsync(await client.GetAsync("/api/auth/me"), HttpStatusCode.Unauthorized);
        var blocked = await AnonymousClient().PostAsJsonAsync("/api/auth/login", new { email = account.Email, password = account.Password }, Json);
        Assert.Equal(HttpStatusCode.Unauthorized, blocked.StatusCode);

        await AsAdministratorAsync(sp => sp.GetRequiredService<AccountService>().SetLockedAsync(id, false));
        await LoginAsync(AnonymousClient(), account);
    }

    [Fact]
    public async Task LockingAnAccountWithoutRotatingTheStamp_WouldNotEndTheSession_WhichIsWhyAccountServiceRotatesIt()
    {
        // Documents the verified Identity behavior: a bare lockout date change leaves the signed-in user signed in.
        var account = Factory.Account("Sales");
        var client = await SignedInClientAsync("Sales");

        await AsAdministratorAsync(async sp =>
        {
            var users = sp.GetRequiredService<UserManager<AppUser>>();
            var user = (await users.FindByEmailAsync(account.Email))!;
            await users.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddHours(1)); // no stamp rotation
        });

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task DeletingAUser_EndsTheirSession()
    {
        var account = Factory.Account("Warehouse");
        var client = await SignedInClientAsync("Warehouse");

        await AsAdministratorAsync(async sp =>
        {
            var users = sp.GetRequiredService<UserManager<AppUser>>();
            await users.DeleteAsync((await users.FindByEmailAsync(account.Email))!);
        });

        await ExpectProblemAsync(await client.GetAsync("/api/auth/me"), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ARoleRemovedWithoutRotatingTheStamp_IsStillReReadOnEveryRequest()
    {
        // The session survives, but permissions come from the database on every request, so the removed role stops working at once.
        var client = await SignedInClientAsync("Admin");
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/products", new { sku = "A-1", name = "Thing", price = 1 }, Json)).StatusCode);

        await AsAdministratorAsync(async sp =>
        {
            var users = sp.GetRequiredService<UserManager<AppUser>>();
            await users.RemoveFromRoleAsync((await users.FindByEmailAsync(Factory.Account("Admin").Email))!, "Admin");
        });

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/products", new { sku = "A-2", name = "Thing", price = 1 }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/products")).StatusCode);
    }

    // ---- Logout ends every session of that user (Step 10E) ------------------------------------------------

    /// <summary>Signs in with a bare client and returns it with the exact Set-Cookie value the server issued.</summary>
    private async Task<(HttpClient Browser, string CookieHeader)> SignedInWithCopyOfCookieAsync(string role)
    {
        var account = Factory.Account(role);
        var bare = RawClient();
        var token = (await ExpectAsync<CsrfTokenResponse>(await bare.GetAsync("/api/auth/csrf"), HttpStatusCode.OK)).Token;
        var login = await bare.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { email = account.Email, password = account.Password }),
            Headers = { { "X-CSRF-TOKEN", token } },
        });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var setCookie = login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("techstore.session=", StringComparison.Ordinal));
        return (bare, setCookie.Split(';')[0]);
    }

    private async Task<HttpStatusCode> MeWithCookieAsync(string cookieHeader)
    {
        using var thief = Factory.CreateDefaultClient(); // a different machine: no cookie jar, only the copied value
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Add("Cookie", cookieHeader);
        return (await thief.SendAsync(request)).StatusCode;
    }

    [Fact]
    public async Task ACopiedSessionCookie_StopsWorking_AfterTheUserLogsOut()
    {
        var (browser, copy) = await SignedInWithCopyOfCookieAsync("Sales");
        Assert.Equal(HttpStatusCode.OK, await MeWithCookieAsync(copy)); // the copy works while the session is alive

        var token = (await ExpectAsync<CsrfTokenResponse>(await browser.GetAsync("/api/auth/csrf"), HttpStatusCode.OK)).Token;
        var logout = await browser.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout") { Headers = { { "X-CSRF-TOKEN", token } } });
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, await MeWithCookieAsync(copy));
    }

    [Fact]
    public async Task LoggingOutInOneBrowser_EndsTheSameUsersSessionInAnotherBrowser()
    {
        var laptop = await SignedInClientAsync("Sales");
        var phone = await SignedInClientAsync("Sales");
        Assert.Equal(HttpStatusCode.OK, (await phone.GetAsync("/api/auth/me")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await laptop.PostAsync("/api/auth/logout", null)).StatusCode);

        await ExpectProblemAsync(await phone.GetAsync("/api/auth/me"), HttpStatusCode.Unauthorized);
        await ExpectProblemAsync(await laptop.GetAsync("/api/auth/me"), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task LoggingOut_DoesNotAffectOtherUsersSessions()
    {
        var sales = await SignedInClientAsync("Sales");
        var warehouse = await SignedInClientAsync("Warehouse");

        await sales.PostAsync("/api/auth/logout", null);

        Assert.Equal(HttpStatusCode.OK, (await warehouse.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync("/api/auth/me")).StatusCode); // the Admin client from the test setup
    }

    [Fact]
    public async Task AfterLoggingOut_TheUserCanSignInAgain_ForAFreshSession()
    {
        var account = Factory.Account("Sales");
        var (browser, oldCopy) = await SignedInWithCopyOfCookieAsync("Sales");
        var token = (await ExpectAsync<CsrfTokenResponse>(await browser.GetAsync("/api/auth/csrf"), HttpStatusCode.OK)).Token;
        await browser.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout") { Headers = { { "X-CSRF-TOKEN", token } } });

        var fresh = AnonymousClient();
        await LoginAsync(fresh, account);

        Assert.Equal(HttpStatusCode.OK, (await fresh.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await MeWithCookieAsync(oldCopy)); // the old cookie stays dead
    }

    [Fact]
    public async Task AnAnonymousLogout_RevokesNothing()
    {
        var (_, victimCopy) = await SignedInWithCopyOfCookieAsync("Sales");

        var attempt = await AnonymousClient().PostAsync("/api/auth/logout", null);

        await ExpectProblemAsync(attempt, HttpStatusCode.Unauthorized);
        Assert.Equal(HttpStatusCode.OK, await MeWithCookieAsync(victimCopy)); // the victim's session is untouched
    }

    [Fact]
    public async Task ALogoutWithoutAValidCsrfToken_RevokesNothing_EvenWithTheRealSessionCookie()
    {
        // What a malicious page could do: make the victim's browser send the cookie, but it cannot know the token.
        var (victim, copy) = await SignedInWithCopyOfCookieAsync("Sales");
        var otherBrowser = await SignedInClientAsync("Sales");

        var noToken = await victim.PostAsync("/api/auth/logout", null);
        var wrongToken = await victim.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout") { Headers = { { "X-CSRF-TOKEN", "forged" } } });

        await ExpectProblemAsync(noToken, HttpStatusCode.BadRequest);
        await ExpectProblemAsync(wrongToken, HttpStatusCode.BadRequest);
        Assert.Equal(HttpStatusCode.OK, await MeWithCookieAsync(copy));
        Assert.Equal(HttpStatusCode.OK, (await otherBrowser.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task ALogoutForgedWithAnotherUsersToken_RevokesNothing()
    {
        var (_, victimCopy) = await SignedInWithCopyOfCookieAsync("Sales");
        var attacker = await SignedInWithCopyOfCookieAsync("Warehouse");
        var attackerToken = (await ExpectAsync<CsrfTokenResponse>(await attacker.Browser.GetAsync("/api/auth/csrf"), HttpStatusCode.OK)).Token;

        // A bare client holding the victim's cookie but the attacker's token.
        using var forged = Factory.CreateDefaultClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout") { Headers = { { "X-CSRF-TOKEN", attackerToken } } };
        request.Headers.Add("Cookie", victimCopy);

        var response = await forged.SendAsync(request);

        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized);
        Assert.Equal(HttpStatusCode.OK, await MeWithCookieAsync(victimCopy));
    }

    [Fact]
    public async Task Logout_StillAnswers204WithNoBody_AndAnExpiringCookie()
    {
        var client = await SignedInClientAsync("Warehouse");

        var logout = await client.PostAsync("/api/auth/logout", null);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Empty(await logout.Content.ReadAsByteArrayAsync());
        Assert.Contains(logout.Headers.GetValues("Set-Cookie"), c => c.StartsWith("techstore.session=", StringComparison.Ordinal) && c.Contains("expires=", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LoggingOutTwice_ThroughTwoBrowsers_SecondGets401LikeAnyEndedSession()
    {
        var first = await SignedInClientAsync("Sales");
        var second = await SignedInClientAsync("Sales");
        await first.PostAsync("/api/auth/logout", null);

        await ExpectProblemAsync(await second.PostAsync("/api/auth/logout", null), HttpStatusCode.Unauthorized);
    }

    // ---- The validation interval is configurable ----------------------------------------------------------

    [Fact]
    public async Task WithALongRevalidationInterval_ASessionIsNotCheckedAgainstTheDatabaseOnEveryRequest()
    {
        await using var relaxed = Factory.WithWebHostBuilder(builder => builder.UseSetting("Auth:SessionRevalidationSeconds", "3600"));
        var account = Factory.Account("Sales");
        var client = relaxed.Browser();
        await LoginAsync(client, account);

        await AsAdministratorAsync(async sp =>
        {
            var users = sp.GetRequiredService<UserManager<AppUser>>();
            var user = (await users.FindByEmailAsync(account.Email))!;
            Assert.True((await users.ChangePasswordAsync(user, account.Password, NewPassword)).Succeeded);
        }, relaxed);

        // Interval 3600 s: the change has not been noticed yet. (With the default of 0 the same change gives 401: see above.)
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("abc")]
    public void AnInvalidRevalidationInterval_StopsTheApiFromStarting(string value)
    {
        using var broken = Factory.WithWebHostBuilder(builder => builder.UseSetting("Auth:SessionRevalidationSeconds", value));

        Assert.ThrowsAny<Exception>(() => broken.Browser());
    }

    // ---- Expiry --------------------------------------------------------------------------------------------

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan time) => _now += time;
    }

    private (WebApplicationFactory<Program> Factory, ManualClock Clock) WithClock()
    {
        var clock = new ManualClock();
        var factory = Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.PostConfigure<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme, options => options.TimeProvider = clock)));
        return (factory, clock);
    }

    [Fact]
    public async Task ASession_ExpiresAfter8Hours_WithoutActivity()
    {
        var (factory, clock) = WithClock();
        await using var _ = factory;
        var client = factory.Browser();
        await LoginAsync(client, Factory.Account("Sales"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);

        clock.Advance(TimeSpan.FromHours(8) + TimeSpan.FromMinutes(1));

        await ExpectProblemAsync(await client.GetAsync("/api/auth/me"), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ASession_JustInsideItsLifetime_StillWorks()
    {
        var (factory, clock) = WithClock();
        await using var _ = factory;
        var client = factory.Browser();
        await LoginAsync(client, Factory.Account("Sales"));

        clock.Advance(TimeSpan.FromHours(7) + TimeSpan.FromMinutes(59));

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task ASession_IsRenewedWhileTheUserIsActive()
    {
        var (factory, clock) = WithClock();
        await using var _ = factory;
        var client = factory.Browser();
        await LoginAsync(client, Factory.Account("Sales"));

        clock.Advance(TimeSpan.FromHours(5));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode); // renews the session
        clock.Advance(TimeSpan.FromHours(5));            // 10 hours after login, but only 5 since the last activity
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);

        clock.Advance(TimeSpan.FromHours(9));            // then a long idle gap
        await ExpectProblemAsync(await client.GetAsync("/api/auth/me"), HttpStatusCode.Unauthorized);
    }

    // ---- Absolute lifetime: an active session still ends after 12 hours (Step 10F) ---------------------------

    [Fact]
    public async Task AnActiveSession_StillEndsAfter12Hours_EvenThoughItIsRenewedByActivity()
    {
        var (factory, clock) = WithClock();
        await using var _ = factory;
        var client = factory.Browser();
        await LoginAsync(client, Factory.Account("Sales"));

        for (var hour = 0; hour < 9; hour += 3) // activity at 3, 6 and 9 hours keeps the 8-hour idle timer fresh
        {
            clock.Advance(TimeSpan.FromHours(3));
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
        }

        clock.Advance(TimeSpan.FromHours(3)); // 12 hours after sign-in, never idle for more than 3
        var ended = await ExpectProblemAsync(await client.GetAsync("/api/auth/me"), HttpStatusCode.Unauthorized);
        Assert.Equal("Sign in to continue.", ended.Detail);
    }

    [Fact]
    public async Task ASession_IsStillValidJustBeforeTheAbsoluteLimit()
    {
        var (factory, clock) = WithClock();
        await using var _ = factory;
        var client = factory.Browser();
        await LoginAsync(client, Factory.Account("Sales"));

        clock.Advance(TimeSpan.FromHours(6));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
        clock.Advance(TimeSpan.FromHours(5) + TimeSpan.FromMinutes(59));

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task TheAbsoluteLimit_IsConfigurable()
    {
        var clock = new ManualClock();
        await using var factory = Factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Auth:SessionMaxHours", "2");
            builder.ConfigureServices(services =>
                services.PostConfigure<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme, options => options.TimeProvider = clock));
        });
        var client = factory.Browser();
        await LoginAsync(client, Factory.Account("Sales"));
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);

        clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1));

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task AfterTheAbsoluteLimit_SigningInAgainStartsAFreshSession()
    {
        var (factory, clock) = WithClock();
        await using var _ = factory;
        var client = factory.Browser();
        var account = Factory.Account("Sales");
        await LoginAsync(client, account);
        clock.Advance(TimeSpan.FromHours(13));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);

        await LoginAsync(client, account);
        clock.Advance(TimeSpan.FromHours(5));

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public void AnInvalidAbsoluteLimit_StopsTheApiFromStarting(string hours)
    {
        using var broken = Factory.WithWebHostBuilder(builder => builder.UseSetting("Auth:SessionMaxHours", hours));

        Assert.ThrowsAny<Exception>(() => broken.Browser());
    }

    [Fact]
    public async Task AnExpiredSession_CanBeReplacedByLoggingInAgain()
    {
        var (factory, clock) = WithClock();
        await using var _ = factory;
        var client = factory.Browser();
        var account = Factory.Account("Sales");
        await LoginAsync(client, account);
        clock.Advance(TimeSpan.FromHours(9));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);

        await LoginAsync(client, account);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
    }
}
