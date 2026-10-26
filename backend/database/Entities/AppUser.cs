using Microsoft.AspNetCore.Identity;

namespace TechStore.Database.Entities;

/// <summary>
/// An employee who can sign in. Identity supplies the e-mail, password hash, security stamp and lockout fields;
/// the e-mail address doubles as the sign-in name. Maps to the "users" table.
/// </summary>
public class AppUser : IdentityUser<long>
{
    public string DisplayName { get; set; } = string.Empty;
}
