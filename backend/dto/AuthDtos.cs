using System.ComponentModel.DataAnnotations;

namespace TechStore.Dto;

/// <summary>Body of POST /api/auth/login. Blank values are rejected with a 400 before any credentials are checked.</summary>
public record LoginRequest(
    [Required(AllowEmptyStrings = false)] string Email,
    [Required(AllowEmptyStrings = false)] string Password);

/// <summary>The anti-forgery token the browser sends back in the X-CSRF-TOKEN header.</summary>
public record CsrfTokenResponse(string Token);

/// <summary>The signed-in user. Permissions are the authorization policies the user satisfies (for example "ManageStock").</summary>
public record CurrentUserResponse(
    long UserId,
    string Email,
    string DisplayName,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions);
