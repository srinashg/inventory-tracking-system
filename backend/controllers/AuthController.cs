using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TechStore.Auth;
using TechStore.Dto;

namespace TechStore.Api;

/// <summary>Sign in, sign out, "who am I" and the CSRF token. All logic is in <see cref="AccountService"/>.</summary>
[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly AccountService _account;
    private readonly IAntiforgery _antiforgery;

    public AuthController(AccountService account, IAntiforgery antiforgery)
    {
        _account = account;
        _antiforgery = antiforgery;
    }

    // The token is tied to the current identity, so the app fetches a new one after login and logout.
    [HttpGet("csrf")]
    [AllowAnonymous]
    public ActionResult<CsrfTokenResponse> Csrf() =>
        Ok(new CsrfTokenResponse(_antiforgery.GetAndStoreTokens(HttpContext).RequestToken!));

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult<CurrentUserResponse>> Login(LoginRequest request) =>
        Ok(await _account.LoginAsync(request.Email, request.Password));

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await _account.LogoutAsync(User); // ends all of this user's sessions, then clears this browser's cookie
        return NoContent();
    }

    [HttpGet("me")]
    public async Task<ActionResult<CurrentUserResponse>> Me() =>
        Ok(await _account.GetCurrentUserAsync(User));
}
