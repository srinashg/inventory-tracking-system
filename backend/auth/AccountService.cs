using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using TechStore.Database.Entities;
using TechStore.Dto;

namespace TechStore.Auth;

/// <summary>
/// Sign-in, sign-out and account changes (see docs/services.md section 15.1). Passwords are hashed and verified by
/// ASP.NET Core Identity; nothing here implements cryptography. Passwords are never logged.
/// </summary>
public class AccountService
{
    private readonly UserManager<AppUser> _users;
    private readonly SignInManager<AppUser> _signIn;
    private readonly IAuthorizationService _authorization;
    private readonly ILogger<AccountService> _logger;

    public AccountService(
        UserManager<AppUser> users,
        SignInManager<AppUser> signIn,
        IAuthorizationService authorization,
        ILogger<AccountService> logger)
    {
        _users = users;
        _signIn = signIn;
        _authorization = authorization;
        _logger = logger;
    }

    /// <summary>
    /// Checks an e-mail and password. A wrong password, an unknown e-mail, blank input and a locked account all throw the
    /// same <see cref="InvalidCredentialsException"/>. Five wrong passwords in a row lock the account for 15 minutes.
    /// </summary>
    public async Task<AppUser> ValidateCredentialsAsync(string email, string password)
    {
        var user = string.IsNullOrWhiteSpace(email) ? null : await _users.FindByEmailAsync(email.Trim());
        if (user is null)
        {
            // Do the same expensive hashing work a real account would cost, so a faster "unknown e-mail" answer
            // does not reveal which e-mail addresses have accounts. The result is thrown away.
            if (!string.IsNullOrEmpty(password))
            {
                _users.PasswordHasher.HashPassword(new AppUser(), password);
            }

            // The typed text is not logged: people sometimes type a password into the e-mail box.
            _logger.LogWarning("Sign-in failed for an unknown account.");
            throw new InvalidCredentialsException();
        }

        if (string.IsNullOrEmpty(password))
        {
            _logger.LogWarning("Sign-in failed for {Email}.", user.Email);
            throw new InvalidCredentialsException();
        }

        // Verifies the password hash and, because lockoutOnFailure is true, counts the failure (or resets the count on success).
        var result = await _signIn.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);
        if (!result.Succeeded)
        {
            _logger.LogWarning("Sign-in failed for {Email}.", user.Email);
            throw new InvalidCredentialsException();
        }

        _logger.LogInformation("Sign-in succeeded for {Email}.", user.Email);
        return user;
    }

    /// <summary>Validates the credentials, then issues the session cookie.</summary>
    public async Task<CurrentUserResponse> LoginAsync(string email, string password)
    {
        var user = await ValidateCredentialsAsync(email, password);
        await _signIn.SignInAsync(user, isPersistent: false); // sets the encrypted, signed, HttpOnly cookie
        return await BuildCurrentUserAsync(user);
    }

    /// <summary>
    /// Signs the user out: ends every session of this user (see <see cref="RevokeSessionsAsync"/>) and clears this
    /// browser's cookie. Only the already-authenticated, CSRF-checked caller can reach it, so nobody can sign out someone else.
    /// </summary>
    public async Task LogoutAsync(ClaimsPrincipal principal)
    {
        await RevokeSessionsAsync(principal);
        await _signIn.SignOutAsync();
    }

    /// <summary>
    /// Rotates the user's security stamp. The stamp is checked on every request, so every copy of this user's session cookie
    /// (on any browser, or one that was stolen) stops working. Other users are not affected.
    /// </summary>
    public async Task RevokeSessionsAsync(ClaimsPrincipal principal)
    {
        var user = await _users.GetUserAsync(principal);
        if (user is null)
        {
            return; // nobody to revoke
        }

        Require(await _users.UpdateSecurityStampAsync(user), "end the user's sessions");
        _logger.LogInformation("User {UserId} signed out; all of the user's sessions were ended.", user.Id);
    }

    /// <summary>The signed-in user with roles and permissions. Permissions are the policies the user satisfies.</summary>
    public async Task<CurrentUserResponse> GetCurrentUserAsync(ClaimsPrincipal principal)
    {
        var user = await _users.GetUserAsync(principal) ?? throw new NotSignedInException();
        return await BuildCurrentUserAsync(user);
    }

    /// <summary>Creates an employee. Used by the bootstrap and test seeding; no endpoint calls it.</summary>
    public async Task<CurrentUserResponse> CreateUserAsync(string email, string displayName, string password, string role)
    {
        var user = await UserProvisioner.CreateAsync(_users, email, displayName, password, role);
        return await BuildCurrentUserAsync(user);
    }

    /// <summary>
    /// Gives a user exactly one role. Identity does not rotate the security stamp for role changes, so it is rotated here:
    /// the user's existing sessions end and the user signs in again with the new role.
    /// </summary>
    public async Task AssignRoleAsync(long userId, string role)
    {
        if (!Roles.All.Contains(role))
        {
            throw new ArgumentException($"'{role}' is not a known role. Use one of: {string.Join(", ", Roles.All)}.", nameof(role));
        }

        var user = await FindAsync(userId);
        var current = await _users.GetRolesAsync(user);
        if (current.Count == 1 && current[0] == role)
        {
            return; // nothing to change, so existing sessions stay valid
        }

        Require(await _users.RemoveFromRolesAsync(user, current.Where(r => r != role)), "remove the old role");
        if (!current.Contains(role))
        {
            Require(await _users.AddToRoleAsync(user, role), "add the new role");
        }

        Require(await _users.UpdateSecurityStampAsync(user), "end the user's sessions");
        _logger.LogInformation("The role of user {UserId} was changed to {Role}; the user's sessions were ended.", user.Id, role);
    }

    /// <summary>
    /// Locks or unlocks an account. Identity does not end existing sessions when an account is locked, so the security stamp
    /// is rotated here: a locked user is signed out on the next request.
    /// </summary>
    public async Task SetLockedAsync(long userId, bool locked)
    {
        var user = await FindAsync(userId);
        if (await _users.IsLockedOutAsync(user) == locked)
        {
            return; // already in that state
        }

        if (locked)
        {
            Require(await _users.SetLockoutEnabledAsync(user, true), "enable lockout");
            Require(await _users.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddYears(100)), "lock the account");
        }
        else
        {
            Require(await _users.SetLockoutEndDateAsync(user, null), "unlock the account");
            Require(await _users.ResetAccessFailedCountAsync(user), "reset the failed sign-in count");
        }

        Require(await _users.UpdateSecurityStampAsync(user), "end the user's sessions");
        _logger.LogInformation("The account of user {UserId} was {State}; the user's sessions were ended.", user.Id, locked ? "locked" : "unlocked");
    }

    private async Task<AppUser> FindAsync(long userId) =>
        await _users.FindByIdAsync(userId.ToString()) ?? throw new AccountNotFoundException(userId);

    private async Task<CurrentUserResponse> BuildCurrentUserAsync(AppUser user)
    {
        var roles = (await _users.GetRolesAsync(user)).OrderBy(r => r).ToList();

        // Evaluate each policy for this user with ASP.NET Core's own authorization service, so the mapping from roles to
        // permissions lives in AuthorizationSetup and nowhere else.
        var principal = await _signIn.CreateUserPrincipalAsync(user);
        var permissions = new List<string>();
        foreach (var policy in Policies.All)
        {
            if ((await _authorization.AuthorizeAsync(principal, policy)).Succeeded)
            {
                permissions.Add(policy);
            }
        }

        return new CurrentUserResponse(user.Id, user.Email!, user.DisplayName, roles, permissions);
    }

    private static void Require(IdentityResult result, string action)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Could not {action}: {string.Join(" ", result.Errors.Select(e => e.Description))}");
        }
    }
}
