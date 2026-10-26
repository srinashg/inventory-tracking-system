using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TechStore.Database;
using TechStore.Database.Entities;

namespace TechStore.Auth;

public static class AuthServiceCollectionExtensions
{
    /// <summary>
    /// Registers everything authentication needs: ASP.NET Core Identity over the existing TechStoreDbContext, the cookie session,
    /// the authorization policies, CSRF protection, and the account services. All security mechanisms are the framework's own.
    /// </summary>
    public static IServiceCollection AddTechStoreAuth(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var section = configuration.GetSection(AuthOptions.SectionName);
        var options = section.Get<AuthOptions>() ?? new AuthOptions();
        if (options.SessionMinutes < 1 || options.SessionMaxHours < 1 || options.SessionRevalidationSeconds < 0)
        {
            throw new InvalidOperationException(
                "Auth:SessionMinutes and Auth:SessionMaxHours must be at least 1, and Auth:SessionRevalidationSeconds must not be negative.");
        }

        if (options.RateLimit.LoginPerClientPerMinute < 1 || options.RateLimit.LoginGlobalPerMinute < 1)
        {
            throw new InvalidOperationException("Auth:RateLimit:LoginPerClientPerMinute and Auth:RateLimit:LoginGlobalPerMinute must be at least 1.");
        }

        services.Configure<AuthOptions>(section);

        // Cookie encryption keys belong to this application name. With DataProtection:KeysPath the key ring is kept in that
        // folder (a volume in Docker), so sessions survive a restart; without it the framework's default location is used.
        var dataProtection = services.AddDataProtection().SetApplicationName("TechStore");
        var keysPath = configuration["DataProtection:KeysPath"];
        if (!string.IsNullOrWhiteSpace(keysPath))
        {
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
        }

        ConfigureTrustedProxies(services, options.TrustedProxies);

        services.AddIdentity<AppUser, AppRole>(identity =>
        {
            // Length matters more than character-class rules.
            identity.Password.RequiredLength = 12;
            identity.Password.RequireDigit = false;
            identity.Password.RequireLowercase = false;
            identity.Password.RequireUppercase = false;
            identity.Password.RequireNonAlphanumeric = false;

            identity.Lockout.AllowedForNewUsers = true;
            identity.Lockout.MaxFailedAccessAttempts = 5;
            identity.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);

            identity.User.RequireUniqueEmail = true;
        })
            .AddEntityFrameworkStores<TechStoreDbContext>()
            .AddPasswordValidator<WeakPasswordValidator>(); // on top of the 12-character rule

        // How often a signed-in session is re-checked against the database (0 = every request).
        services.Configure<SecurityStampValidatorOptions>(validator =>
            validator.ValidationInterval = TimeSpan.FromSeconds(options.SessionRevalidationSeconds));

        var securePolicy = ResolveCookieSecurePolicy(options, environment);

        services.ConfigureApplicationCookie(cookie =>
        {
            cookie.Cookie.Name = "techstore.session";
            cookie.Cookie.HttpOnly = true; // JavaScript cannot read the session cookie
            cookie.Cookie.SameSite = SameSiteMode.Strict;
            cookie.Cookie.SecurePolicy = securePolicy;
            cookie.Cookie.Path = "/";
            cookie.ExpireTimeSpan = TimeSpan.FromMinutes(options.SessionMinutes);
            cookie.SlidingExpiration = true;

            // The idle timeout above renews while the user is active; this absolute limit does not. The moment of
            // sign-in is stored in the (encrypted, signed) cookie and every request is checked against it.
            var maxAge = TimeSpan.FromHours(options.SessionMaxHours);
            var securityStampCheck = cookie.Events.OnValidatePrincipal; // Identity's own security-stamp check
            cookie.Events.OnSigningIn = context =>
            {
                var now = (context.Options.TimeProvider ?? TimeProvider.System).GetUtcNow();
                context.Properties.Items[SessionStartedKey] = now.ToString("O");
                return Task.CompletedTask;
            };
            cookie.Events.OnValidatePrincipal = async context =>
            {
                var now = (context.Options.TimeProvider ?? TimeProvider.System).GetUtcNow();
                var started = context.Properties.Items.TryGetValue(SessionStartedKey, out var text) && DateTimeOffset.TryParse(text, out var parsed)
                    ? parsed
                    : (DateTimeOffset?)null;

                if (started is null || now - started.Value >= maxAge)
                {
                    context.RejectPrincipal(); // too old (or from before this rule existed): sign in again
                    await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
                    return;
                }

                if (securityStampCheck is not null)
                {
                    await securityStampCheck(context);
                }
            };

            // An API answers with a status code and a problem+json body, never a redirect to a login page.
            cookie.Events.OnRedirectToLogin = context =>
                WriteProblemAsync(context.HttpContext, StatusCodes.Status401Unauthorized, "Unauthorized", "Sign in to continue.");
            cookie.Events.OnRedirectToAccessDenied = context =>
                WriteProblemAsync(context.HttpContext, StatusCodes.Status403Forbidden, "Forbidden", "Your role does not allow this action.");
        });

        services.AddAuthorization(AuthorizationSetup.Configure);

        services.AddAntiforgery(antiforgery =>
        {
            antiforgery.HeaderName = AntiforgeryValidationFilter.HeaderName;
            antiforgery.Cookie.Name = "techstore.csrf";
            antiforgery.Cookie.SameSite = SameSiteMode.Strict;
            antiforgery.Cookie.SecurePolicy = securePolicy;
        });
        services.Configure<MvcOptions>(mvc => mvc.Filters.Add<AntiforgeryValidationFilter>());

        // Sign-in attempts are limited per client address and, as a cap against password spraying, across all clients.
        // The client address is the connection's own address. Forwarded headers are NOT trusted here (they can be forged);
        // behind a reverse proxy the deployment must configure trusted forwarded headers (docs/deployment-security-checklist.md).
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                PartitionedRateLimiter.Create<HttpContext, string>(context => IsLogin(context)
                    ? RateLimitPartition.GetFixedWindowLimiter($"client:{context.Connection.RemoteIpAddress}", _ => PerMinute(options.RateLimit.LoginPerClientPerMinute))
                    : RateLimitPartition.GetNoLimiter("not-login")),
                PartitionedRateLimiter.Create<HttpContext, string>(context => IsLogin(context)
                    ? RateLimitPartition.GetFixedWindowLimiter("all-clients", _ => PerMinute(options.RateLimit.LoginGlobalPerMinute))
                    : RateLimitPartition.GetNoLimiter("not-login")));
            limiter.OnRejected = async (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
                }

                await WriteProblemAsync(context.HttpContext, StatusCodes.Status429TooManyRequests, "Too Many Requests",
                    "Too many sign-in attempts. Please wait a minute and try again.");
            };
        });

        services.AddScoped<AuthInitializer>();
        services.AddScoped<AccountService>(); // constructor dependencies arrive with the implementation

        return services;
    }

    /// <summary>
    /// Plain http is accepted for local development and tests, or when it is explicitly allowed for local use. Everywhere else
    /// the cookies require https, and an unsafe combination stops the API from starting.
    /// </summary>
    private static CookieSecurePolicy ResolveCookieSecurePolicy(AuthOptions options, IHostEnvironment environment)
    {
        var isLocalEnvironment = environment.IsDevelopment() || environment.IsEnvironment("Testing");
        var require = options.RequireSecureCookie ?? !isLocalEnvironment;

        if (require)
        {
            return CookieSecurePolicy.Always;
        }

        if (!isLocalEnvironment && !options.AllowInsecureCookiesForLocalHttp)
        {
            throw new InvalidOperationException(
                "Auth:RequireSecureCookie is false, which sends the session cookie over plain http. That is only acceptable for " +
                "local use. If this really is a local-only setup, also set Auth:AllowInsecureCookiesForLocalHttp to true; " +
                "otherwise remove Auth:RequireSecureCookie.");
        }

        return CookieSecurePolicy.SameAsRequest;
    }

    /// <summary>
    /// X-Forwarded-For is believed only from the listed proxy addresses, only for the client address, and only its last entry
    /// (the one the trusted proxy wrote). Without any listed proxy nothing is configured and the header is ignored.
    /// </summary>
    private static void ConfigureTrustedProxies(IServiceCollection services, List<string> trustedProxies)
    {
        if (trustedProxies.Count == 0)
        {
            return;
        }

        var addresses = trustedProxies.Select(value =>
            IPAddress.TryParse(value, out var address)
                ? address
                : throw new InvalidOperationException($"Auth:TrustedProxies must list single IP addresses; '{value}' is not one.")).ToList();

        services.Configure<ForwardedHeadersOptions>(forwarded =>
        {
            forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
            forwarded.ForwardLimit = 1;
            forwarded.KnownProxies.Clear();
            forwarded.KnownIPNetworks.Clear(); // by default the whole loopback range is trusted; here only the listed addresses are
            foreach (var address in addresses) forwarded.KnownProxies.Add(address);
        });
    }

    /// <summary>Key in the session cookie that records when the user signed in.</summary>
    private const string SessionStartedKey = "techstore.session_started";

    private static bool IsLogin(HttpContext context) =>
        HttpMethods.IsPost(context.Request.Method) && context.Request.Path.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase);

    private static FixedWindowRateLimiterOptions PerMinute(int permits) =>
        new() { PermitLimit = permits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true };

    private static Task WriteProblemAsync(HttpContext context, int status, string title, string detail)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        return context.Response.WriteAsJsonAsync(
            new ProblemDetails { Status = status, Title = title, Detail = detail }, options: null, contentType: "application/problem+json");
    }
}
