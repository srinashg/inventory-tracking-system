using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TechStore.Auth.Tests.Infrastructure;
using TechStore.Database.Entities;

namespace TechStore.Auth.Tests;

public class AccountServiceTests : AuthTestBase
{
    public AccountServiceTests(AuthDatabaseFixture database) : base(database)
    {
    }

    /// <summary>A password of exactly this length that is not trivially weak (many different characters).</summary>
    private static string VariedPassword(int length) => new string(Enumerable.Range(0, length - 1).Select(i => (char)('k' + i % 12)).ToArray()) + "9";

    private static string RandomPassword() => $"pw-{Guid.NewGuid():N}-{Guid.NewGuid():N}";

    /// <summary>A host in the "Testing" environment with the roles created and one Admin test account.</summary>
    private async Task<AuthTestHost> HostAsync()
    {
        var host = AuthTestHost.Create(Database, "Testing", new()
        {
            ["Auth:TestAccounts:0:Email"] = "admin@test.local",
            ["Auth:TestAccounts:0:Password"] = RandomPassword(),
            ["Auth:TestAccounts:0:Role"] = "Admin",
        });
        await host.InitializeAsync();
        return host;
    }

    /// <summary>Runs one operation in its own scope, like one web request.</summary>
    private static async Task<T> RunAsync<T>(AuthTestHost host, Func<AccountService, Task<T>> action)
    {
        await using var scope = host.Scope();
        return await action(scope.ServiceProvider.GetRequiredService<AccountService>());
    }

    private static Task RunAsync(AuthTestHost host, Func<AccountService, Task> action) =>
        RunAsync<object?>(host, async service => { await action(service); return null; });

    private static async Task<(string Email, string Password)> CreateAsync(AuthTestHost host, string role, string email = "person@example.com")
    {
        var password = RandomPassword();
        await RunAsync(host, service => service.CreateUserAsync(email, "Some Person", password, role));
        return (email, password);
    }

    private async Task<AppUser> ReloadAsync(AuthTestHost host, string email) => (await host.FindUserAsync(email))!;

    // ======================================================================================================
    // CreateUser
    // ======================================================================================================

    [Fact]
    public async Task CreateUser_StoresAHashedPasswordAndTheRole_AndReturnsTheUser()
    {
        await using var host = await HostAsync();
        var password = RandomPassword();

        var created = await RunAsync(host, s => s.CreateUserAsync("Amelia@Example.com", "Amelia Reed", password, "Sales"));

        Assert.Equal("Amelia@Example.com", created.Email);
        Assert.Equal("Amelia Reed", created.DisplayName);
        Assert.Equal(new[] { "Sales" }, created.Roles);
        Assert.Equal(new[] { "ManageOrders" }, created.Permissions);
        var stored = await ReloadAsync(host, "amelia@example.com");
        Assert.NotEqual(password, stored.PasswordHash);
        Assert.True(await host.PasswordIsAsync("amelia@example.com", password));
    }

    [Theory]
    [InlineData(11, false)]
    [InlineData(12, true)]
    public async Task CreateUser_PasswordMustBeAtLeast12Characters(int length, bool accepted)
    {
        await using var host = await HostAsync();
        var password = VariedPassword(length);

        if (accepted)
        {
            await RunAsync(host, s => s.CreateUserAsync("a@example.com", "A", password, "Sales"));
            Assert.True(await host.PasswordIsAsync("a@example.com", password));
        }
        else
        {
            var ex = await Assert.ThrowsAsync<AccountCreationException>(() => RunAsync(host, s => s.CreateUserAsync("a@example.com", "A", password, "Sales")));
            Assert.Contains("12", ex.Message);
            Assert.DoesNotContain(password, ex.Message);
            Assert.Null(await host.FindUserAsync("a@example.com"));
        }
    }

    [Fact]
    public async Task CreateUser_DuplicateEmail_IsRejected_EvenWithDifferentCase()
    {
        await using var host = await HostAsync();
        await CreateAsync(host, "Sales", "dup@example.com");

        await Assert.ThrowsAsync<AccountCreationException>(() =>
            RunAsync(host, s => s.CreateUserAsync("DUP@example.com", "Other", RandomPassword(), "Warehouse")));

        Assert.Equal(2, await host.UserCountAsync()); // the Admin test account and the first dup@
    }

    [Theory]
    [InlineData("SuperUser")]
    [InlineData("")]
    [InlineData("admin")] // role names are exact
    public async Task CreateUser_UnknownRole_IsRejected_AndNoUserRemains(string role)
    {
        await using var host = await HostAsync();

        await Assert.ThrowsAsync<AccountCreationException>(() => RunAsync(host, s => s.CreateUserAsync("x@example.com", "X", RandomPassword(), role)));

        Assert.Null(await host.FindUserAsync("x@example.com"));
    }

    [Fact]
    public async Task CreateUser_BlankEmail_IsRejected()
    {
        await using var host = await HostAsync();

        await Assert.ThrowsAsync<AccountCreationException>(() => RunAsync(host, s => s.CreateUserAsync("  ", "X", RandomPassword(), "Sales")));
    }

    // ======================================================================================================
    // Obviously weak passwords are refused (length is checked separately, see the 11/12 test above)
    // ======================================================================================================

    [Theory]
    [InlineData("aaaaaaaaaaaaaaaa")]            // one character repeated
    [InlineData("abababababababab")]            // only two different characters
    [InlineData("MyPassword-2026-ok")]          // contains "password"
    [InlineData("Qwerty-Keyboard-2026")]        // contains "qwerty"
    [InlineData("amelia.reed-is-here-1")]       // contains the part of the email before the @
    public async Task CreateUser_ObviouslyWeakPasswords_AreRefused_WithoutEchoingThem(string password)
    {
        await using var host = await HostAsync();

        var ex = await Assert.ThrowsAsync<AccountCreationException>(() =>
            RunAsync(host, s => s.CreateUserAsync("amelia.reed@example.com", "Amelia", password, "Sales")));

        Assert.DoesNotContain(password, ex.Message);
        Assert.Null(await host.FindUserAsync("amelia.reed@example.com"));
        Assert.DoesNotContain(host.Logs.Messages, m => m.Contains(password));
    }

    [Fact]
    public async Task CreateUser_AStrongRandomPassword_IsAccepted()
    {
        await using var host = await HostAsync();

        await RunAsync(host, s => s.CreateUserAsync("amelia.reed@example.com", "Amelia", RandomPassword(), "Sales"));

        Assert.NotNull(await host.FindUserAsync("amelia.reed@example.com"));
    }

    [Fact]
    public async Task CreateUser_WeakPasswordMessage_SaysWhatToDo()
    {
        await using var host = await HostAsync();

        var ex = await Assert.ThrowsAsync<AccountCreationException>(() =>
            RunAsync(host, s => s.CreateUserAsync("a@example.com", "A", "Password-Password-1", "Sales")));

        Assert.Contains("too easy to guess", ex.Message);
    }

    // ======================================================================================================
    // ValidateCredentials
    // ======================================================================================================

    [Fact]
    public async Task ValidateCredentials_CorrectPassword_ReturnsTheUser_IgnoringEmailCase()
    {
        await using var host = await HostAsync();
        var (email, password) = await CreateAsync(host, "Sales");

        var user = await RunAsync(host, s => s.ValidateCredentialsAsync(email.ToUpperInvariant(), password));

        Assert.Equal(email, user.Email);
    }

    [Fact]
    public async Task ValidateCredentials_WrongPasswordUnknownEmailAndBlankValues_AllLookIdentical()
    {
        await using var host = await HostAsync();
        var (email, password) = await CreateAsync(host, "Sales");

        var wrongPassword = await Assert.ThrowsAsync<InvalidCredentialsException>(() => RunAsync(host, s => s.ValidateCredentialsAsync(email, password + "x")));
        var unknownEmail = await Assert.ThrowsAsync<InvalidCredentialsException>(() => RunAsync(host, s => s.ValidateCredentialsAsync("nobody@example.com", password)));
        var blankEmail = await Assert.ThrowsAsync<InvalidCredentialsException>(() => RunAsync(host, s => s.ValidateCredentialsAsync("", password)));
        var blankPassword = await Assert.ThrowsAsync<InvalidCredentialsException>(() => RunAsync(host, s => s.ValidateCredentialsAsync(email, "")));

        Assert.All(new[] { wrongPassword, unknownEmail, blankEmail, blankPassword }, e => Assert.Equal("Invalid email or password.", e.Message));
    }

    [Fact]
    public async Task ValidateCredentials_TheFifthConsecutiveFailureLocksTheAccountForAbout15Minutes()
    {
        await using var host = await HostAsync();
        var (email, password) = await CreateAsync(host, "Sales");

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            await Assert.ThrowsAsync<InvalidCredentialsException>(() => RunAsync(host, s => s.ValidateCredentialsAsync(email, "wrong-password-" + attempt)));
        }

        var user = await ReloadAsync(host, email);
        Assert.NotNull(user.LockoutEnd);
        var locked = user.LockoutEnd!.Value - DateTimeOffset.UtcNow;
        Assert.InRange(locked, TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(15.1));

        // Even the correct password is refused while locked, with the same message.
        var ex = await Assert.ThrowsAsync<InvalidCredentialsException>(() => RunAsync(host, s => s.ValidateCredentialsAsync(email, password)));
        Assert.Equal("Invalid email or password.", ex.Message);
    }

    [Fact]
    public async Task ValidateCredentials_FourFailuresAreNotEnoughToLock_AndASuccessResetsTheCount()
    {
        await using var host = await HostAsync();
        var (email, password) = await CreateAsync(host, "Sales");

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            await Assert.ThrowsAsync<InvalidCredentialsException>(() => RunAsync(host, s => s.ValidateCredentialsAsync(email, "wrong-password-" + attempt)));
        }

        Assert.Equal(4, (await ReloadAsync(host, email)).AccessFailedCount);
        await RunAsync(host, s => s.ValidateCredentialsAsync(email, password));
        Assert.Equal(0, (await ReloadAsync(host, email)).AccessFailedCount);

        // Four more failures after the reset still do not lock it.
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            await Assert.ThrowsAsync<InvalidCredentialsException>(() => RunAsync(host, s => s.ValidateCredentialsAsync(email, "wrong-again-" + attempt)));
        }

        Assert.Null((await ReloadAsync(host, email)).LockoutEnd);
        await RunAsync(host, s => s.ValidateCredentialsAsync(email, password));
    }

    [Fact]
    public async Task ValidateCredentials_NeverLogsPasswordsOrTextTypedIntoTheEmailBox()
    {
        await using var host = await HostAsync();
        var (email, password) = await CreateAsync(host, "Sales");
        var wrongPassword = "wrong-" + Guid.NewGuid().ToString("N");
        var mistypedEmail = "typed-a-password-here-" + Guid.NewGuid().ToString("N");

        await RunAsync(host, s => s.ValidateCredentialsAsync(email, password));
        await Assert.ThrowsAsync<InvalidCredentialsException>(() => RunAsync(host, s => s.ValidateCredentialsAsync(email, wrongPassword)));
        await Assert.ThrowsAsync<InvalidCredentialsException>(() => RunAsync(host, s => s.ValidateCredentialsAsync(mistypedEmail, wrongPassword)));

        var logs = host.Logs.Messages;
        Assert.DoesNotContain(logs, m => m.Contains(password) || m.Contains(wrongPassword) || m.Contains(mistypedEmail));
        Assert.Contains(logs, m => m.Contains("succeeded") && m.Contains(email));
        Assert.Contains(logs, m => m.Contains("failed") && m.Contains(email));
        Assert.Contains(logs, m => m.Contains("failed") && m.Contains("unknown account"));
    }

    // ======================================================================================================
    // Timing: an unknown e-mail must cost the same as a wrong password
    // ======================================================================================================

    /// <summary>Counts how often a password is hashed or checked: the expensive step that makes sign-in take measurable time.</summary>
    private sealed class CountingHasher : IPasswordHasher<AppUser>
    {
        private readonly PasswordHasher<AppUser> _inner = new();
        public int Calls;

        public string HashPassword(AppUser user, string password) { Interlocked.Increment(ref Calls); return _inner.HashPassword(user, password); }

        public PasswordVerificationResult VerifyHashedPassword(AppUser user, string hashedPassword, string providedPassword)
        { Interlocked.Increment(ref Calls); return _inner.VerifyHashedPassword(user, hashedPassword, providedPassword); }
    }

    [Fact]
    public async Task ValidateCredentials_DoesTheSameHashingWorkForAnUnknownEmailAsForAWrongPassword()
    {
        var hasher = new CountingHasher();
        await using var host = AuthTestHost.Create(Database, "Testing", new()
        {
            ["Auth:TestAccounts:0:Email"] = "admin@test.local",
            ["Auth:TestAccounts:0:Password"] = RandomPassword(),
            ["Auth:TestAccounts:0:Role"] = "Admin",
        }, configure: services => services.Replace(ServiceDescriptor.Singleton<IPasswordHasher<AppUser>>(hasher)));
        await host.InitializeAsync();

        hasher.Calls = 0;
        await Assert.ThrowsAsync<InvalidCredentialsException>(() => RunAsync(host, s => s.ValidateCredentialsAsync("admin@test.local", "wrong-password-123")));
        var forAWrongPassword = hasher.Calls;

        hasher.Calls = 0;
        await Assert.ThrowsAsync<InvalidCredentialsException>(() => RunAsync(host, s => s.ValidateCredentialsAsync("nobody@test.local", "wrong-password-123")));
        var forAnUnknownEmail = hasher.Calls;

        Assert.True(forAWrongPassword >= 1);
        Assert.Equal(forAWrongPassword, forAnUnknownEmail); // otherwise response time would reveal which e-mails have accounts
    }

    // ======================================================================================================
    // GetCurrentUser (roles and permissions)
    // ======================================================================================================

    [Theory]
    [InlineData("Admin", new[] { "ManageProducts", "ManageStock", "ManageOrders", "FulfillOrders" })]
    [InlineData("Sales", new[] { "ManageOrders" })]
    [InlineData("Warehouse", new[] { "ManageStock", "FulfillOrders" })]
    public async Task GetCurrentUser_ReportsTheRoleAndExactlyThePermissionsItGrants(string role, string[] expectedPermissions)
    {
        await using var host = await HostAsync();
        var (email, _) = await CreateAsync(host, role);
        var principal = await PrincipalForAsync(host, email);

        var current = await RunAsync(host, s => s.GetCurrentUserAsync(principal));

        Assert.Equal(email, current.Email);
        Assert.Equal("Some Person", current.DisplayName);
        Assert.Equal(new[] { role }, current.Roles);
        Assert.Equal(expectedPermissions, current.Permissions);
        Assert.True(current.UserId > 0);
    }

    [Fact]
    public async Task GetCurrentUser_WhenTheAccountNoLongerExists_IsNotSignedIn()
    {
        await using var host = await HostAsync();
        var (email, _) = await CreateAsync(host, "Sales");
        var principal = await PrincipalForAsync(host, email);
        await using (var scope = host.Scope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            await users.DeleteAsync((await users.FindByEmailAsync(email))!);
        }

        await Assert.ThrowsAsync<NotSignedInException>(() => RunAsync(host, s => s.GetCurrentUserAsync(principal)));
    }

    [Fact]
    public async Task GetCurrentUser_ForAnAnonymousPrincipal_IsNotSignedIn()
    {
        await using var host = await HostAsync();

        await Assert.ThrowsAsync<NotSignedInException>(() => RunAsync(host, s => s.GetCurrentUserAsync(new ClaimsPrincipal(new ClaimsIdentity()))));
    }

    private static async Task<ClaimsPrincipal> PrincipalForAsync(AuthTestHost host, string email)
    {
        await using var scope = host.Scope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var signIn = scope.ServiceProvider.GetRequiredService<SignInManager<AppUser>>();
        return await signIn.CreateUserPrincipalAsync((await users.FindByEmailAsync(email))!);
    }

    // ======================================================================================================
    // RevokeSessions: logging out ends every session of that user (the security stamp rotates)
    // ======================================================================================================

    [Fact]
    public async Task RevokeSessions_RotatesTheSecurityStampOfTheSignedInUser_AndNobodyElses()
    {
        await using var host = await HostAsync();
        var (email, _) = await CreateAsync(host, "Sales", "me@example.com");
        var (otherEmail, _) = await CreateAsync(host, "Warehouse", "other@example.com");
        var before = await ReloadAsync(host, email);
        var otherBefore = await ReloadAsync(host, otherEmail);
        var principal = await PrincipalForAsync(host, email);

        await RunAsync(host, s => s.RevokeSessionsAsync(principal));

        Assert.NotEqual(before.SecurityStamp, (await ReloadAsync(host, email)).SecurityStamp);
        Assert.Equal(otherBefore.SecurityStamp, (await ReloadAsync(host, otherEmail)).SecurityStamp);
    }

    [Fact]
    public async Task RevokeSessions_CalledTwice_RotatesEachTime()
    {
        await using var host = await HostAsync();
        var (email, _) = await CreateAsync(host, "Sales");
        var principal = await PrincipalForAsync(host, email);

        await RunAsync(host, s => s.RevokeSessionsAsync(principal));
        var first = (await ReloadAsync(host, email)).SecurityStamp;
        await RunAsync(host, s => s.RevokeSessionsAsync(principal));

        Assert.NotEqual(first, (await ReloadAsync(host, email)).SecurityStamp);
    }

    [Fact]
    public async Task RevokeSessions_ForAnAnonymousPrincipal_ChangesNothing()
    {
        await using var host = await HostAsync();
        var (email, _) = await CreateAsync(host, "Sales");
        var before = await ReloadAsync(host, email);

        await RunAsync(host, s => s.RevokeSessionsAsync(new ClaimsPrincipal(new ClaimsIdentity())));

        Assert.Equal(before.SecurityStamp, (await ReloadAsync(host, email)).SecurityStamp);
    }

    [Fact]
    public async Task RevokeSessions_IsLogged_WithTheUserId_AndNoPassword()
    {
        await using var host = await HostAsync();
        var (email, password) = await CreateAsync(host, "Sales");
        var user = await ReloadAsync(host, email);

        await RunAsync(host, s => s.RevokeSessionsAsync(PrincipalForAsync(host, email).Result));

        Assert.Contains(host.Logs.Messages, m => m.Contains("signed out") && m.Contains(user.Id.ToString()));
        Assert.DoesNotContain(host.Logs.Messages, m => m.Contains(password));
    }

    // ======================================================================================================
    // AssignRole: administrator changes a role, existing sessions end (the security stamp rotates)
    // ======================================================================================================

    [Fact]
    public async Task AssignRole_ReplacesTheRole_AndRotatesTheSecurityStamp()
    {
        await using var host = await HostAsync();
        var (email, _) = await CreateAsync(host, "Sales");
        var before = await ReloadAsync(host, email);

        await RunAsync(host, s => s.AssignRoleAsync(before.Id, "Warehouse"));

        var after = await ReloadAsync(host, email);
        Assert.Equal(new[] { "Warehouse" }, await host.RolesOfAsync(email));
        Assert.NotEqual(before.SecurityStamp, after.SecurityStamp);
    }

    [Fact]
    public async Task AssignRole_RemovesEveryOtherRole()
    {
        await using var host = await HostAsync();
        var (email, _) = await CreateAsync(host, "Sales");
        await using (var scope = host.Scope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            await users.AddToRoleAsync((await users.FindByEmailAsync(email))!, "Warehouse"); // a second role, added behind the service's back
        }

        await RunAsync(host, s => s.AssignRoleAsync(( ReloadAsync(host, email).Result).Id, "Admin"));

        Assert.Equal(new[] { "Admin" }, await host.RolesOfAsync(email));
    }

    [Fact]
    public async Task AssignRole_ToTheRoleTheUserAlreadyHas_ChangesNothing()
    {
        await using var host = await HostAsync();
        var (email, _) = await CreateAsync(host, "Sales");
        var before = await ReloadAsync(host, email);

        await RunAsync(host, s => s.AssignRoleAsync(before.Id, "Sales"));

        Assert.Equal(before.SecurityStamp, (await ReloadAsync(host, email)).SecurityStamp);
    }

    [Fact]
    public async Task AssignRole_UnknownRole_IsRejected_AndNothingChanges()
    {
        await using var host = await HostAsync();
        var (email, _) = await CreateAsync(host, "Sales");
        var before = await ReloadAsync(host, email);

        await Assert.ThrowsAsync<ArgumentException>(() => RunAsync(host, s => s.AssignRoleAsync(before.Id, "SuperUser")));

        Assert.Equal(new[] { "Sales" }, await host.RolesOfAsync(email));
        Assert.Equal(before.SecurityStamp, (await ReloadAsync(host, email)).SecurityStamp);
    }

    [Fact]
    public async Task AssignRole_UnknownUser_ThrowsNotFound()
    {
        await using var host = await HostAsync();

        await Assert.ThrowsAsync<AccountNotFoundException>(() => RunAsync(host, s => s.AssignRoleAsync(9999, "Sales")));
    }

    // ======================================================================================================
    // SetLocked: administrator locks or unlocks an account, existing sessions end
    // ======================================================================================================

    [Fact]
    public async Task SetLocked_True_LocksTheAccount_RotatesTheStamp_AndBlocksSignIn()
    {
        await using var host = await HostAsync();
        var (email, password) = await CreateAsync(host, "Sales");
        var before = await ReloadAsync(host, email);

        await RunAsync(host, s => s.SetLockedAsync(before.Id, true));

        var after = await ReloadAsync(host, email);
        Assert.True(after.LockoutEnd > DateTimeOffset.UtcNow.AddYears(1));
        Assert.NotEqual(before.SecurityStamp, after.SecurityStamp);
        await Assert.ThrowsAsync<InvalidCredentialsException>(() => RunAsync(host, s => s.ValidateCredentialsAsync(email, password)));
    }

    [Fact]
    public async Task SetLocked_False_UnlocksTheAccount_ResetsFailures_AndRotatesTheStamp()
    {
        await using var host = await HostAsync();
        var (email, password) = await CreateAsync(host, "Sales");
        var user = await ReloadAsync(host, email);
        await RunAsync(host, s => s.SetLockedAsync(user.Id, true));
        var locked = await ReloadAsync(host, email);

        await RunAsync(host, s => s.SetLockedAsync(user.Id, false));

        var unlocked = await ReloadAsync(host, email);
        Assert.Null(unlocked.LockoutEnd);
        Assert.Equal(0, unlocked.AccessFailedCount);
        Assert.NotEqual(locked.SecurityStamp, unlocked.SecurityStamp);
        await RunAsync(host, s => s.ValidateCredentialsAsync(email, password));
    }

    [Fact]
    public async Task SetLocked_UnlockingAnAutomaticallyLockedAccount_Works()
    {
        await using var host = await HostAsync();
        var (email, password) = await CreateAsync(host, "Sales");
        for (var i = 0; i < 5; i++)
        {
            await Assert.ThrowsAsync<InvalidCredentialsException>(() => RunAsync(host, s => s.ValidateCredentialsAsync(email, "wrong-password-" + i)));
        }

        await RunAsync(host, s => s.SetLockedAsync((ReloadAsync(host, email).Result).Id, false));

        await RunAsync(host, s => s.ValidateCredentialsAsync(email, password));
    }

    [Fact]
    public async Task SetLocked_WhenAlreadyInThatState_ChangesNothing()
    {
        await using var host = await HostAsync();
        var (email, _) = await CreateAsync(host, "Sales");
        var user = await ReloadAsync(host, email);

        await RunAsync(host, s => s.SetLockedAsync(user.Id, false)); // not locked, "unlock" is a no-op
        Assert.Equal(user.SecurityStamp, (await ReloadAsync(host, email)).SecurityStamp);

        await RunAsync(host, s => s.SetLockedAsync(user.Id, true));
        var lockedStamp = (await ReloadAsync(host, email)).SecurityStamp;
        await RunAsync(host, s => s.SetLockedAsync(user.Id, true)); // already locked
        Assert.Equal(lockedStamp, (await ReloadAsync(host, email)).SecurityStamp);
    }

    [Fact]
    public async Task SetLocked_UnknownUser_ThrowsNotFound()
    {
        await using var host = await HostAsync();

        await Assert.ThrowsAsync<AccountNotFoundException>(() => RunAsync(host, s => s.SetLockedAsync(9999, true)));
    }

    [Fact]
    public async Task AdminChanges_AreLogged_WithoutAnyPassword()
    {
        await using var host = await HostAsync();
        var (email, password) = await CreateAsync(host, "Sales");
        var user = await ReloadAsync(host, email);

        await RunAsync(host, s => s.AssignRoleAsync(user.Id, "Warehouse"));
        await RunAsync(host, s => s.SetLockedAsync(user.Id, true));

        Assert.Contains(host.Logs.Messages, m => m.Contains("role") && m.Contains("Warehouse") && m.Contains(user.Id.ToString()));
        Assert.Contains(host.Logs.Messages, m => m.Contains("locked") && m.Contains(user.Id.ToString()));
        Assert.DoesNotContain(host.Logs.Messages, m => m.Contains(password));
    }
}
