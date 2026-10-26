using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using TechStore.Database;
using TechStore.Database.Entities;

namespace TechStore.Auth;

/// <summary>
/// Runs once at startup (see docs/services.md section 15.2): makes sure the roles exist, creates test accounts when running
/// as "Testing", and makes sure an Admin exists. It never logs a password and never puts one in an exception message.
/// </summary>
public class AuthInitializer
{
    private const string TestingEnvironment = "Testing";
    private const int MaxRecommendedRevalidationSeconds = 60;

    private readonly TechStoreDbContext _db;
    private readonly UserManager<AppUser> _users;
    private readonly RoleManager<AppRole> _roles;
    private readonly AuthOptions _options;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<AuthInitializer> _logger;

    public AuthInitializer(
        TechStoreDbContext db,
        UserManager<AppUser> users,
        RoleManager<AppRole> roles,
        IOptions<AuthOptions> options,
        IHostEnvironment environment,
        ILogger<AuthInitializer> logger)
    {
        _db = db;
        _users = users;
        _roles = roles;
        _options = options.Value;
        _environment = environment;
        _logger = logger;
    }

    public async Task InitializeAsync()
    {
        // Test accounts are a testing convenience: refuse to start anywhere else rather than quietly create them.
        if (_options.TestAccounts.Count > 0 && !_environment.IsEnvironment(TestingEnvironment))
        {
            throw new InvalidOperationException(
                $"Test accounts are configured (Auth:TestAccounts) but the environment is '{_environment.EnvironmentName}'. " +
                $"Test accounts are only allowed in the '{TestingEnvironment}' environment. Remove them and restart.");
        }

        if (_options.SessionRevalidationSeconds > MaxRecommendedRevalidationSeconds)
        {
            _logger.LogWarning(
                "Auth:SessionRevalidationSeconds is {Seconds}. Role changes, locks, password changes and logouts only end existing sessions " +
                "after that long. The recommended value is 0 (check on every request); values above {Limit} weaken revocation.",
                _options.SessionRevalidationSeconds, MaxRecommendedRevalidationSeconds);
        }

        if (_options.RequireSecureCookie == false && !_environment.IsDevelopment() && !_environment.IsEnvironment(TestingEnvironment))
        {
            _logger.LogWarning(
                "Local-only http cookie mode is on (Auth:RequireSecureCookie=false with Auth:AllowInsecureCookiesForLocalHttp=true): " +
                "sign-in cookies are not protected by https. Use this only on one computer and never expose this instance to a network.");
        }

        // An unreachable database must not stop the API from starting, so /health can report the problem.
        if (!await _db.Database.CanConnectAsync())
        {
            _logger.LogWarning("Account initialization was skipped because the database cannot be reached. It runs again on the next start.");
            return;
        }

        try
        {
            await EnsureRolesAsync();
        }
        catch (Exception ex) when (IsMissingTable(ex))
        {
            throw new InvalidOperationException(
                "The authentication tables do not exist in the database. Apply the database migrations " +
                "(dotnet ef database update --project backend/database) and restart.");
        }

        await CreateTestAccountsAsync();
        await EnsureAdminAsync();
    }

    /// <summary>PostgreSQL error 42P01: a table the query needs is not there (the migrations have not been applied).</summary>
    private static bool IsMissingTable(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.UndefinedTable })
            {
                return true;
            }
        }

        return false;
    }

    private async Task EnsureRolesAsync()
    {
        foreach (var role in Roles.All)
        {
            if (!await _roles.RoleExistsAsync(role))
            {
                var created = await _roles.CreateAsync(new AppRole(role));
                if (!created.Succeeded)
                {
                    throw new InvalidOperationException($"Could not create the '{role}' role: {Describe(created)}");
                }
            }
        }
    }

    private async Task CreateTestAccountsAsync()
    {
        foreach (var account in _options.TestAccounts)
        {
            if (string.IsNullOrWhiteSpace(account.Email) || string.IsNullOrEmpty(account.Password) || string.IsNullOrWhiteSpace(account.Role))
            {
                throw new InvalidOperationException("Every Auth:TestAccounts entry needs an Email, a Password and a Role.");
            }

            if (await _users.FindByEmailAsync(account.Email) is not null)
            {
                continue; // already there: never overwrite
            }

            try
            {
                await UserProvisioner.CreateAsync(_users, account.Email, account.Email, account.Password, account.Role);
            }
            catch (AccountCreationException ex)
            {
                throw new InvalidOperationException($"Could not create a test account: {ex.Message}");
            }
        }
    }

    private async Task EnsureAdminAsync()
    {
        if ((await _users.GetUsersInRoleAsync(Roles.Admin)).Count > 0)
        {
            if (!string.IsNullOrWhiteSpace(_options.InitialAdmin.Email) || !string.IsNullOrEmpty(_options.InitialAdmin.Password))
            {
                // Never print the values. They are ignored now, but a password left in the environment is still a secret lying around.
                _logger.LogWarning(
                    "The Auth:InitialAdmin settings are configured but not needed because an administrator already exists. " +
                    "Remove them from the environment or user-secrets.");
            }

            return; // an Admin exists: bootstrap settings, if any, are ignored
        }

        var bootstrap = _options.InitialAdmin;
        if (string.IsNullOrWhiteSpace(bootstrap.Email) || string.IsNullOrEmpty(bootstrap.Password))
        {
            throw new InvalidOperationException(
                "No administrator account exists and no initial administrator is configured. " +
                "Set Auth:InitialAdmin:Email and Auth:InitialAdmin:Password (environment variables or user-secrets) and restart.");
        }

        try
        {
            await UserProvisioner.CreateAsync(
                _users, bootstrap.Email, string.IsNullOrWhiteSpace(bootstrap.DisplayName) ? "Administrator" : bootstrap.DisplayName,
                bootstrap.Password, Roles.Admin);
        }
        catch (AccountCreationException ex)
        {
            // The message states which rule was broken; it never contains the password.
            throw new InvalidOperationException($"The initial administrator could not be created: {ex.Message}");
        }

        _logger.LogInformation("The initial administrator account was created.");
    }

    private static string Describe(IdentityResult result) => string.Join(" ", result.Errors.Select(e => e.Description));
}
