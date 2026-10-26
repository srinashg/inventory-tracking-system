using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TechStore.Auth.Tests.Infrastructure;
using TechStore.Database.Entities;

namespace TechStore.Auth.Tests;

public class AuthInitializerTests : AuthTestBase
{
    public AuthInitializerTests(AuthDatabaseFixture database) : base(database)
    {
    }

    /// <summary>A throw-away password. Tests never use a fixed password, so nothing secret-looking is committed.</summary>
    /// <summary>A password of exactly this length that is not trivially weak (many different characters).</summary>
    private static string VariedPassword(int length) => new string(Enumerable.Range(0, length - 1).Select(i => (char)('k' + i % 12)).ToArray()) + "9";

    private static string RandomPassword() => $"pw-{Guid.NewGuid():N}-{Guid.NewGuid():N}";

    private static Dictionary<string, string?> BootstrapSettings(string email, string? password, string? displayName = null)
    {
        var settings = new Dictionary<string, string?> { ["Auth:InitialAdmin:Email"] = email, ["Auth:InitialAdmin:Password"] = password };
        if (displayName is not null) settings["Auth:InitialAdmin:DisplayName"] = displayName;
        return settings;
    }

    private static Dictionary<string, string?> TestAccountSettings(params (string Email, string Password, string Role)[] accounts)
    {
        var settings = new Dictionary<string, string?>();
        for (var i = 0; i < accounts.Length; i++)
        {
            settings[$"Auth:TestAccounts:{i}:Email"] = accounts[i].Email;
            settings[$"Auth:TestAccounts:{i}:Password"] = accounts[i].Password;
            settings[$"Auth:TestAccounts:{i}:Role"] = accounts[i].Role;
        }

        return settings;
    }

    private async Task<AuthTestHost> HostAsync(string environment, Dictionary<string, string?>? settings = null)
    {
        return AuthTestHost.Create(Database, environment, settings);
    }

    /// <summary>Creates an employee straight through Identity, bypassing the initializer.</summary>
    private async Task SeedUserAsync(AuthTestHost host, string email, string role)
    {
        await using var scope = host.Scope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<AppRole>>();
        if (!await roles.RoleExistsAsync(role)) await roles.CreateAsync(new AppRole(role));
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = new AppUser { UserName = email, Email = email, DisplayName = email };
        Assert.True((await users.CreateAsync(user, RandomPassword())).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
    }

    // ======================================================================================================
    // Roles
    // ======================================================================================================

    [Fact]
    public async Task CreatesTheThreeRoles_AndDoesNotDuplicateThemOnTheNextStart()
    {
        await using var host = await HostAsync("Production", BootstrapSettings("admin@example.com", RandomPassword()));

        await host.InitializeAsync();
        await host.InitializeAsync();

        await using var scope = host.Scope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<AppRole>>().Roles.Select(r => r.Name).OrderBy(n => n).ToList();
        Assert.Equal(new[] { "Admin", "Sales", "Warehouse" }, roles);
    }

    // ======================================================================================================
    // First Admin (bootstrap)
    // ======================================================================================================

    [Fact]
    public async Task NoAdminAndNoBootstrapSettings_FailsStartupWithAClearMessage()
    {
        await using var host = await HostAsync("Production");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(host.InitializeAsync);

        Assert.Contains("No administrator account exists", ex.Message);
        Assert.Contains("Auth:InitialAdmin:Email", ex.Message);
        Assert.Contains("Auth:InitialAdmin:Password", ex.Message);
        Assert.Equal(0, await host.UserCountAsync());
    }

    [Fact]
    public async Task NoAdminAndOnlyAnEmail_FailsStartup_WithoutEchoingTheEmail()
    {
        await using var host = await HostAsync("Production", BootstrapSettings("someone.private@example.com", password: null));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(host.InitializeAsync);

        Assert.Contains("Auth:InitialAdmin:Password", ex.Message);
        Assert.DoesNotContain("someone.private@example.com", ex.Message);
        Assert.Equal(0, await host.UserCountAsync());
    }

    [Fact]
    public async Task NoAdminAndOnlyAPassword_FailsStartup_WithoutEchoingThePassword()
    {
        var password = RandomPassword();
        await using var host = await HostAsync("Production", BootstrapSettings(email: "", password));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(host.InitializeAsync);

        Assert.Contains("Auth:InitialAdmin:Email", ex.Message);
        Assert.DoesNotContain(password, ex.Message);
        Assert.DoesNotContain(host.Logs.Messages, m => m.Contains(password));
    }

    [Fact]
    public async Task AdminAlreadyExists_StartupSucceedsWithoutBootstrapSettings()
    {
        await using var host = await HostAsync("Production");
        await SeedUserAsync(host, "existing.admin@example.com", Roles.Admin);

        await host.InitializeAsync();

        Assert.Equal(1, await host.UserCountAsync());
    }

    [Fact]
    public async Task AdminAlreadyExists_BootstrapSettingsAreIgnored_AndTheExistingAccountIsUntouched()
    {
        var newPassword = RandomPassword();
        await using var host = await HostAsync("Production", BootstrapSettings("second.admin@example.com", newPassword));
        await SeedUserAsync(host, "existing.admin@example.com", Roles.Admin);
        var before = await host.FindUserAsync("existing.admin@example.com");

        await host.InitializeAsync();

        Assert.Equal(1, await host.UserCountAsync());
        Assert.Null(await host.FindUserAsync("second.admin@example.com"));
        var after = await host.FindUserAsync("existing.admin@example.com");
        Assert.Equal(before!.PasswordHash, after!.PasswordHash);
        Assert.Equal(before.SecurityStamp, after.SecurityStamp);
    }

    [Fact]
    public async Task OnlyANonAdminUserExists_StillRequiresBootstrapSettings()
    {
        await using var host = await HostAsync("Production");
        await SeedUserAsync(host, "sales@example.com", Roles.Sales);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(host.InitializeAsync);

        Assert.Contains("No administrator account exists", ex.Message);
    }

    [Fact]
    public async Task BootstrapCreatesExactlyOneAdmin_WhosePasswordVerifies_AndIsStoredOnlyAsAHash()
    {
        var password = RandomPassword();
        await using var host = await HostAsync("Production", BootstrapSettings("First.Admin@Example.com", password, "First Admin"));

        await host.InitializeAsync();

        Assert.Equal(1, await host.UserCountAsync());
        var admin = await host.FindUserAsync("first.admin@example.com"); // look-ups ignore case
        Assert.NotNull(admin);
        Assert.Equal("First Admin", admin.DisplayName);
        Assert.Equal(new[] { "Admin" }, await host.RolesOfAsync("first.admin@example.com"));
        Assert.True(await host.PasswordIsAsync("first.admin@example.com", password));
        Assert.False(await host.PasswordIsAsync("first.admin@example.com", password + "x"));
        Assert.NotEqual(password, admin.PasswordHash);
        Assert.DoesNotContain(password, admin.PasswordHash!);
        Assert.True(admin.LockoutEnabled);
    }

    [Fact]
    public async Task BootstrapWithoutADisplayName_UsesAReadableDefault()
    {
        await using var host = await HostAsync("Production", BootstrapSettings("admin@example.com", RandomPassword()));

        await host.InitializeAsync();

        Assert.Equal("Administrator", (await host.FindUserAsync("admin@example.com"))!.DisplayName);
    }

    [Fact]
    public async Task RunningBootstrapAgain_DoesNotCreateAnotherAdmin()
    {
        await using var host = await HostAsync("Production", BootstrapSettings("admin@example.com", RandomPassword()));

        await host.InitializeAsync();
        await host.InitializeAsync();

        Assert.Equal(1, await host.UserCountAsync());
    }

    [Fact]
    public async Task BootstrapNeverLogsThePassword_OnlyThatAnAdminWasCreated()
    {
        var password = RandomPassword();
        await using var host = await HostAsync("Production", BootstrapSettings("admin@example.com", password));

        await host.InitializeAsync();

        Assert.DoesNotContain(host.Logs.Messages, message => message.Contains(password));
        var user = await host.FindUserAsync("admin@example.com");
        Assert.DoesNotContain(host.Logs.Messages, message => message.Contains(user!.PasswordHash!));
        Assert.Contains(host.Logs.Messages, message => message.Contains("administrator account was created", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(11, false)]
    [InlineData(12, true)]
    public async Task BootstrapPasswordMustBeAtLeast12Characters(int length, bool accepted)
    {
        var password = VariedPassword(length);
        await using var host = await HostAsync("Production", BootstrapSettings("admin@example.com", password));

        if (accepted)
        {
            await host.InitializeAsync();
            Assert.Equal(1, await host.UserCountAsync());
        }
        else
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(host.InitializeAsync);
            Assert.Contains("12", ex.Message);
            Assert.DoesNotContain(password, ex.Message);
            Assert.DoesNotContain(host.Logs.Messages, message => message.Contains(password));
            Assert.Equal(0, await host.UserCountAsync());
        }
    }

    [Fact]
    public async Task BootstrapWithAnInvalidEmail_FailsStartup()
    {
        await using var host = await HostAsync("Production", BootstrapSettings("not-an-email", RandomPassword()));

        await Assert.ThrowsAsync<InvalidOperationException>(host.InitializeAsync);

        Assert.Equal(0, await host.UserCountAsync());
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    [InlineData("Testing")]
    public async Task BootstrapWorksInEveryEnvironment(string environment)
    {
        await using var host = await HostAsync(environment, BootstrapSettings("admin@example.com", RandomPassword()));

        await host.InitializeAsync();

        Assert.Equal(new[] { "Admin" }, await host.RolesOfAsync("admin@example.com"));
    }

    // ======================================================================================================
    // Test accounts: Testing environment only
    // ======================================================================================================

    [Fact]
    public async Task InTesting_TheConfiguredTestAccountsAreCreatedWithTheirRoles_WhichAlsoSatisfiesTheAdminCheck()
    {
        var (adminPw, salesPw, warehousePw) = (RandomPassword(), RandomPassword(), RandomPassword());
        await using var host = await HostAsync("Testing", TestAccountSettings(
            ("admin@test.local", adminPw, "Admin"), ("sales@test.local", salesPw, "Sales"), ("warehouse@test.local", warehousePw, "Warehouse")));

        await host.InitializeAsync(); // no bootstrap settings, yet no startup failure: an Admin test account exists

        Assert.Equal(3, await host.UserCountAsync());
        Assert.Equal(new[] { "Admin" }, await host.RolesOfAsync("admin@test.local"));
        Assert.Equal(new[] { "Sales" }, await host.RolesOfAsync("sales@test.local"));
        Assert.Equal(new[] { "Warehouse" }, await host.RolesOfAsync("warehouse@test.local"));
        Assert.True(await host.PasswordIsAsync("sales@test.local", salesPw));
        Assert.DoesNotContain(host.Logs.Messages, m => m.Contains(adminPw) || m.Contains(salesPw) || m.Contains(warehousePw));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task OutsideTesting_ConfiguredTestAccountsFailStartup_AndNothingIsCreated(string environment)
    {
        var settings = TestAccountSettings(("admin@test.local", RandomPassword(), "Admin"));
        foreach (var (key, value) in BootstrapSettings("real.admin@example.com", RandomPassword())) settings[key] = value;
        await using var host = await HostAsync(environment, settings);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(host.InitializeAsync);

        Assert.Contains("Testing", ex.Message);
        Assert.Equal(0, await host.UserCountAsync()); // not even the legitimate bootstrap Admin: startup stopped first
    }

    [Fact]
    public async Task InTesting_WithoutAnyConfiguration_NoAccountsAppear_AndTheAdminRuleStillApplies()
    {
        await using var host = await HostAsync("Testing");

        await Assert.ThrowsAsync<InvalidOperationException>(host.InitializeAsync);

        Assert.Equal(0, await host.UserCountAsync());
    }

    [Fact]
    public async Task TestAccounts_AreNotRecreatedOrOverwritten_OnTheNextStart()
    {
        var password = RandomPassword();
        await using var host = await HostAsync("Testing", TestAccountSettings(("admin@test.local", password, "Admin")));
        await host.InitializeAsync();
        var before = await host.FindUserAsync("admin@test.local");

        await host.InitializeAsync();

        Assert.Equal(1, await host.UserCountAsync());
        Assert.Equal(before!.PasswordHash, (await host.FindUserAsync("admin@test.local"))!.PasswordHash);
    }

    [Fact]
    public async Task ATestAccountWithAnUnknownRole_FailsStartup()
    {
        await using var host = await HostAsync("Testing", TestAccountSettings(("boss@test.local", RandomPassword(), "SuperUser")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(host.InitializeAsync);

        Assert.Contains("role", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await host.UserCountAsync());
    }

    [Fact]
    public async Task ATestAccountWithAWeakPassword_FailsStartup_WithoutEchoingIt()
    {
        await using var host = await HostAsync("Testing", TestAccountSettings(("admin@test.local", "short1", "Admin")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(host.InitializeAsync);

        Assert.DoesNotContain("short1", ex.Message);
    }

    // ======================================================================================================
    // Startup warnings (never containing a secret)
    // ======================================================================================================

    [Theory]
    [InlineData("0", false)]
    [InlineData("60", false)]
    [InlineData("61", true)]
    [InlineData("3600", true)]
    public async Task ALongSessionRevalidationInterval_LogsAWarning(string seconds, bool warns)
    {
        var settings = BootstrapSettings("admin@example.com", RandomPassword());
        settings["Auth:SessionRevalidationSeconds"] = seconds;
        await using var host = await HostAsync("Production", settings);

        await host.InitializeAsync();

        var warned = host.Logs.Messages.Any(m => m.Contains("[Warning]") && m.Contains("SessionRevalidationSeconds"));
        Assert.Equal(warns, warned);
    }

    [Theory]
    [InlineData("Production", "false", "true", true)]   // local-only http mode on a real environment: loud warning
    [InlineData("Production", null, null, false)]       // secure defaults: nothing to warn about
    [InlineData("Production", "true", "true", false)]   // explicitly secure
    [InlineData("Development", null, null, false)]      // plain http is normal while developing
    public async Task LocalOnlyHttpCookieMode_LogsAWarning_OnlyWhenItIsSwitchedOnOutsideDevelopment(
        string environment, string? requireSecure, string? allowInsecure, bool warns)
    {
        var settings = BootstrapSettings("admin@example.com", RandomPassword());
        if (requireSecure is not null) settings["Auth:RequireSecureCookie"] = requireSecure;
        if (allowInsecure is not null) settings["Auth:AllowInsecureCookiesForLocalHttp"] = allowInsecure;
        await using var host = await HostAsync(environment, settings);

        await host.InitializeAsync();

        var warning = host.Logs.Messages.SingleOrDefault(m => m.Contains("[Warning]") && m.Contains("AllowInsecureCookiesForLocalHttp"));
        Assert.Equal(warns, warning is not null);
        if (warning is not null) Assert.Contains("never expose", warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BootstrapSettingsThatAreNoLongerNeeded_LogAWarning_WithoutTheirValues()
    {
        var password = RandomPassword();
        await using var host = await HostAsync("Production", BootstrapSettings("boss@example.com", password));
        await host.InitializeAsync(); // creates the Admin
        host.Logs.Clear();

        await host.InitializeAsync(); // second start: an Admin exists, the settings are now unused

        var warning = Assert.Single(host.Logs.Messages, m => m.Contains("[Warning]") && m.Contains("Auth:InitialAdmin"));
        Assert.Contains("remove", warning, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(password, warning);
        Assert.DoesNotContain("boss@example.com", warning);
    }

    [Fact]
    public async Task NoBootstrapWarning_WhenTheSettingsWereNeverConfigured()
    {
        await using var host = await HostAsync("Testing", TestAccountSettings(("admin@test.local", RandomPassword(), "Admin")));

        await host.InitializeAsync();
        await host.InitializeAsync();

        Assert.DoesNotContain(host.Logs.Messages, m => m.Contains("[Warning]") && m.Contains("Auth:InitialAdmin"));
    }

    [Fact]
    public async Task NoBootstrapWarning_OnTheStartThatCreatesTheAdmin()
    {
        await using var host = await HostAsync("Production", BootstrapSettings("boss@example.com", RandomPassword()));

        await host.InitializeAsync();

        Assert.DoesNotContain(host.Logs.Messages, m => m.Contains("[Warning]") && m.Contains("Auth:InitialAdmin"));
    }

    // ======================================================================================================
    // Unreachable database
    // ======================================================================================================

    [Fact]
    public async Task WhenTheDatabaseCannotBeReached_StartupContinues_WithAWarning()
    {
        await using var host = AuthTestHost.Create(Database, "Production", BootstrapSettings("admin@example.com", RandomPassword()),
            connectionString: "Host=localhost;Port=1;Database=nothing;Username=postgres;Timeout=2;Command Timeout=2");

        await host.InitializeAsync(); // does not throw: /health reports the problem instead

        Assert.Contains(host.Logs.Messages, m => m.Contains("[Warning]") && m.Contains("skipped", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WhenTheMigrationsHaveNotBeenApplied_StartupFailsWithAMessageSayingSo()
    {
        // A brand-new, empty database: it exists and is reachable, but has no users or roles tables.
        var admin = new NpgsqlConnectionStringBuilder(Database.ConnectionString) { Database = "postgres", Pooling = false };
        const string emptyDatabase = "techstore_auth_unmigrated";
        await using (var connection = new NpgsqlConnection(admin.ConnectionString))
        {
            await connection.OpenAsync();
            await new NpgsqlCommand($"DROP DATABASE IF EXISTS {emptyDatabase}", connection).ExecuteNonQueryAsync();
            await new NpgsqlCommand($"CREATE DATABASE {emptyDatabase}", connection).ExecuteNonQueryAsync();
        }

        try
        {
            var emptyConnection = new NpgsqlConnectionStringBuilder(Database.ConnectionString) { Database = emptyDatabase }.ConnectionString;
            await using var host = AuthTestHost.Create(Database, "Production", BootstrapSettings("admin@example.com", RandomPassword()), emptyConnection);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(host.InitializeAsync);

            Assert.Contains("migrations", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("dotnet ef database update", ex.Message);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var connection = new NpgsqlConnection(admin.ConnectionString);
            await connection.OpenAsync();
            await new NpgsqlCommand($"DROP DATABASE IF EXISTS {emptyDatabase}", connection).ExecuteNonQueryAsync();
        }
    }
}
