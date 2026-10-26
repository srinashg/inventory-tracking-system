namespace TechStore.Auth;

/// <summary>Settings from the "Auth" configuration section. See docs/api-contract.md section 10.8.</summary>
public class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>The idle timeout: how long a session lasts without activity (renewed while the user is active).</summary>
    public int SessionMinutes { get; set; } = 480;

    /// <summary>The absolute limit: a session ends this many hours after sign-in however active the user is.</summary>
    public int SessionMaxHours { get; set; } = 12;

    /// <summary>
    /// Whether the session and CSRF cookies require HTTPS. Not set = required everywhere except the Development and Testing
    /// environments. Turning it off anywhere else also needs <see cref="AllowInsecureCookiesForLocalHttp"/>.
    /// </summary>
    public bool? RequireSecureCookie { get; set; }

    /// <summary>
    /// Explicit acknowledgement that the app is served over plain http for local use only (for example Docker Compose on
    /// a developer's machine). It does nothing on its own; it only lets <see cref="RequireSecureCookie"/> be false outside development.
    /// </summary>
    public bool AllowInsecureCookiesForLocalHttp { get; set; }

    /// <summary>
    /// Addresses of reverse proxies (single IP addresses, not networks) whose X-Forwarded-For header is believed, so the
    /// login rate limit sees the real client. Empty = the header is ignored. See docs/deployment-security-checklist.md.
    /// </summary>
    public List<string> TrustedProxies { get; set; } = new();

    /// <summary>Limits on sign-in attempts.</summary>
    public RateLimitOptions RateLimit { get; set; } = new();

    /// <summary>How often the security stamp is re-checked against the database. 0 = on every request.</summary>
    public int SessionRevalidationSeconds { get; set; }

    /// <summary>Bootstrap for the first Admin. Supplied through environment variables or user-secrets, never committed.</summary>
    public InitialAdminOptions InitialAdmin { get; set; } = new();

    /// <summary>Accounts for automated tests. Honored only when the environment is "Testing".</summary>
    public List<TestAccountOptions> TestAccounts { get; set; } = new();
}

public class RateLimitOptions
{
    /// <summary>Sign-in attempts allowed per minute from one client (network address).</summary>
    public int LoginPerClientPerMinute { get; set; } = 10;

    /// <summary>Sign-in attempts allowed per minute across all clients: a cap against password spraying from many addresses.</summary>
    public int LoginGlobalPerMinute { get; set; } = 100;
}

public class InitialAdminOptions
{
    public string? Email { get; set; }
    public string? Password { get; set; }
    public string? DisplayName { get; set; }
}

public class TestAccountOptions
{
    public string? Email { get; set; }
    public string? Password { get; set; }
    public string? Role { get; set; }
}
