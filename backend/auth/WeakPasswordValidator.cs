using Microsoft.AspNetCore.Identity;
using TechStore.Database.Entities;

namespace TechStore.Auth;

/// <summary>
/// A small extra check on top of Identity's length rule (12 characters): refuses passwords that are obviously easy to guess.
/// Deliberately short: three simple rules, no large word list. The error messages never contain the password.
/// </summary>
public class WeakPasswordValidator : IPasswordValidator<AppUser>
{
    private const int MinimumDifferentCharacters = 5;
    private const int MinimumNamePartLength = 4;

    // Fragments that appear in a large share of breached passwords.
    private static readonly string[] CommonFragments = { "password", "passw0rd", "qwerty", "letmein", "12345678" };

    public Task<IdentityResult> ValidateAsync(UserManager<AppUser> manager, AppUser user, string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return Task.FromResult(IdentityResult.Success); // Identity's own required-length rule reports an empty password
        }

        var errors = new List<IdentityError>();

        if (password.Distinct().Count() < MinimumDifferentCharacters)
        {
            errors.Add(Weak($"Use at least {MinimumDifferentCharacters} different characters."));
        }

        if (CommonFragments.Any(fragment => password.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add(Weak("Do not use common words such as 'password' or 'qwerty'."));
        }

        // The part of the e-mail before the @ is the first thing anyone would try.
        var namePart = user.Email?.Split('@')[0];
        if (namePart is { Length: >= MinimumNamePartLength } && password.Contains(namePart, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(Weak("Do not include your email name in your password."));
        }

        return Task.FromResult(errors.Count == 0 ? IdentityResult.Success : IdentityResult.Failed(errors.ToArray()));
    }

    private static IdentityError Weak(string advice) =>
        new() { Code = "WeakPassword", Description = $"This password is too easy to guess. {advice}" };
}
