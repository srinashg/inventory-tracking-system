using Microsoft.AspNetCore.Identity;
using TechStore.Database.Entities;

namespace TechStore.Auth;

/// <summary>An employee account could not be created. The messages describe the problem and never contain the password.</summary>
public class AccountCreationException : Exception
{
    public AccountCreationException(IEnumerable<string> problems) : base(string.Join(" ", problems))
    {
    }
}

/// <summary>Creates an employee through ASP.NET Core Identity (which hashes the password). Shared by AccountService and AuthInitializer.</summary>
internal static class UserProvisioner
{
    public static async Task<AppUser> CreateAsync(
        UserManager<AppUser> users, string email, string displayName, string password, string role)
    {
        if (!Roles.All.Contains(role))
        {
            throw new AccountCreationException(new[] { $"'{role}' is not a known role. Use one of: {string.Join(", ", Roles.All)}." });
        }

        var user = new AppUser { UserName = email.Trim(), Email = email.Trim(), DisplayName = displayName.Trim() };

        var created = await users.CreateAsync(user, password); // validates the password policy, then stores only the salted hash
        if (!created.Succeeded)
        {
            throw new AccountCreationException(created.Errors.Select(e => e.Description));
        }

        var assigned = await users.AddToRoleAsync(user, role);
        if (!assigned.Succeeded)
        {
            await users.DeleteAsync(user); // do not leave a user without a role behind
            throw new AccountCreationException(assigned.Errors.Select(e => e.Description));
        }

        return user;
    }
}
