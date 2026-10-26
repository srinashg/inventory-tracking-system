using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TechStore.Api.Tests.Infrastructure;
using TechStore.Database.Entities;

namespace TechStore.Api.Tests;

/// <summary>Credentials stay out of logs and the repository; production behaves differently from testing where it must.</summary>
public class SecurityApiTests : ApiTestBase
{
    public SecurityApiTests(ApiFactory factory) : base(factory)
    {
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _messages = new();

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messages) return _messages.ToList();
            }
        }

        public ILogger CreateLogger(string categoryName) => new Capture(categoryName, _messages);

        public void Dispose()
        {
        }

        private sealed class Capture(string category, List<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var text = $"[{logLevel}] {category}: {formatter(state, exception)}" + (exception is null ? "" : " " + exception);
                lock (messages) messages.Add(text);
            }
        }
    }

    // ---- Logs ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task SigningIn_NeverWritesPasswordsOrHashesToTheLogs()
    {
        var logs = new CapturingLoggerProvider();
        await using var factory = Factory.WithWebHostBuilder(builder => builder.ConfigureLogging(logging => logging.AddProvider(logs).SetMinimumLevel(LogLevel.Trace)));
        var account = Factory.Account("Sales");
        var wrongPassword = "wrong-" + Guid.NewGuid().ToString("N");
        var textTypedIntoTheEmailBox = "my-password-is-" + Guid.NewGuid().ToString("N");
        var client = factory.Browser();

        await client.PostAsJsonAsync("/api/auth/login", new { email = account.Email, password = account.Password }, Json);
        await client.PostAsJsonAsync("/api/auth/login", new { email = account.Email, password = wrongPassword }, Json);
        await client.PostAsJsonAsync("/api/auth/login", new { email = textTypedIntoTheEmailBox, password = wrongPassword }, Json);

        string hash;
        await using (var db = Factory.CreateDbContext())
        {
            hash = await db.Users.Where(u => u.Email == account.Email).Select(u => u.PasswordHash!).SingleAsync();
        }

        var all = string.Join("\n", logs.Messages);
        Assert.DoesNotContain(account.Password, all);
        Assert.DoesNotContain(wrongPassword, all);
        Assert.DoesNotContain(textTypedIntoTheEmailBox, all);
        Assert.DoesNotContain(hash, all);
        Assert.Contains("Sign-in succeeded", all);
        Assert.Contains("Sign-in failed", all);
    }

    [Fact]
    public async Task StartingTheApi_NeverWritesTheTestAccountPasswordsToTheLogs()
    {
        var logs = new CapturingLoggerProvider();
        await using var factory = Factory.WithWebHostBuilder(builder => builder.ConfigureLogging(logging => logging.AddProvider(logs).SetMinimumLevel(LogLevel.Trace)));

        factory.Browser();

        var all = string.Join("\n", logs.Messages);
        foreach (var account in Factory.Accounts)
        {
            Assert.DoesNotContain(account.Password, all);
        }
    }

    // ---- Nothing secret is committed -----------------------------------------------------------------------

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TechStore.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Could not find the repository root (TechStore.slnx).");
    }

    private static IEnumerable<(string Path, string Key, string Value)> Flatten(string path, JsonElement element, string prefix = "")
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                    foreach (var item in Flatten(path, property.Value, prefix == "" ? property.Name : $"{prefix}:{property.Name}")) yield return item;
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                    foreach (var flat in Flatten(path, item, $"{prefix}:{index++}")) yield return flat;
                break;
            case JsonValueKind.String:
                yield return (path, prefix, element.GetString() ?? "");
                break;
        }
    }

    [Fact]
    public void CommittedConfigurationFiles_ContainNoPasswordsKeysOrTokens()
    {
        var root = RepositoryRoot();
        var files = Directory.EnumerateFiles(Path.Combine(root, "backend"), "*.json", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "frontend", "e2e"), "*.json", SearchOption.AllDirectories))
            .Concat(new[] { Path.Combine(root, "frontend", "package.json") })
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !path.Contains("node_modules"))
            .ToList();
        Assert.NotEmpty(files);

        var secretKey = new Regex("password|secret|token|apikey|api_key|privatekey", RegexOptions.IgnoreCase);
        var problems = new List<string>();

        foreach (var file in files)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
            foreach (var (path, key, value) in Flatten(Path.GetRelativePath(root, file), document.RootElement))
            {
                if (secretKey.IsMatch(key) && !string.IsNullOrWhiteSpace(value))
                    problems.Add($"{path}: '{key}' has a value");
                if (Regex.IsMatch(value, @"(^|;)\s*(password|pwd)\s*=", RegexOptions.IgnoreCase))
                    problems.Add($"{path}: '{key}' is a connection string containing a password");
            }
        }

        Assert.Empty(problems);
    }

    [Fact]
    public void NoSourceFileInTheRepository_HardCodesTheInitialAdminPassword()
    {
        var root = RepositoryRoot();
        var sources = new[] { "backend", "frontend/src", "frontend/e2e", "tests" }
            .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(root, folder), "*.*", SearchOption.AllDirectories))
            .Where(path => path.EndsWith(".cs") || path.EndsWith(".ts") || path.EndsWith(".tsx") || path.EndsWith(".mjs") || path.EndsWith(".json"))
            .Where(path => !path.Contains("/obj/") && !path.Contains("/bin/") && !path.Contains("node_modules") && !path.EndsWith("SecurityApiTests.cs"))
            .ToList();

        // Looks for  InitialAdmin ... Password ... = "<literal>"  assignments (reading it from configuration is fine).
        var hardCoded = new Regex(@"InitialAdmin[^\n]{0,40}Password[^\n]{0,20}[=:]\s*""[^""\s]{4,}""");
        var offenders = sources.Where(path => hardCoded.IsMatch(File.ReadAllText(path))).Select(path => Path.GetRelativePath(root, path)).ToList();

        Assert.Empty(offenders);
    }

    // ---- Production behaves differently from testing -----------------------------------------------------

    private async Task ClearUsersAsync()
    {
        await using var db = Factory.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE user_roles, user_claims, user_logins, user_tokens, role_claims, users, roles RESTART IDENTITY CASCADE");
    }

    private WebApplicationFactory<Program> Production(Dictionary<string, string?> settings) =>
        new ProductionFactory(Factory.ConnectionString, settings);

    private sealed class ProductionFactory : WebApplicationFactory<Program>
    {
        private readonly string _connection;
        private readonly Dictionary<string, string?> _settings;

        public ProductionFactory(string connection, Dictionary<string, string?> settings)
        {
            _connection = connection;
            _settings = settings;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("ConnectionStrings:TechStore", _connection);
            foreach (var (key, value) in _settings) builder.UseSetting(key, value);
        }
    }

    private static string Everything(Exception ex) => string.Join(" | ", Chain(ex).Select(e => e.Message));

    private static IEnumerable<Exception> Chain(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException) yield return ex;
    }

    [Fact]
    public async Task InProduction_WithNoAdminAndNoBootstrapSettings_TheApiRefusesToStart_WithAClearMessage()
    {
        await ClearUsersAsync();
        await using var production = Production(new());

        var ex = Assert.ThrowsAny<Exception>(() => production.Browser());

        var message = Everything(ex);
        Assert.Contains("No administrator account exists", message);
        Assert.Contains("Auth:InitialAdmin:Password", message);
    }

    [Fact]
    public async Task InProduction_BootstrapCreatesTheFirstAdmin_ThenTheApiStartsWithoutBootstrapSettings()
    {
        await ClearUsersAsync();
        var password = $"pw-{Guid.NewGuid():N}-{Guid.NewGuid():N}";
        await using (var first = Production(new() { ["Auth:InitialAdmin:Email"] = "boss@example.com", ["Auth:InitialAdmin:Password"] = password }))
        {
            first.Browser();
        }

        await using var second = Production(new()); // an Admin exists, so no bootstrap settings are needed
        var client = second.Browser(baseAddress: new Uri("https://localhost"));

        var login = await client.PostAsJsonAsync("/api/auth/login", new { email = "boss@example.com", password }, Json);

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task InProduction_TheSessionCookieIsMarkedSecure()
    {
        await ClearUsersAsync();
        var password = $"pw-{Guid.NewGuid():N}-{Guid.NewGuid():N}";
        await using var production = Production(new() { ["Auth:InitialAdmin:Email"] = "boss@example.com", ["Auth:InitialAdmin:Password"] = password });
        var client = production.Browser(baseAddress: new Uri("https://localhost"));

        var login = await client.PostAsJsonAsync("/api/auth/login", new { email = "boss@example.com", password }, Json);

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var cookie = login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("techstore.session=")).ToLowerInvariant();
        Assert.Contains("secure", cookie.Replace("samesite", ""));
        Assert.Contains("httponly", cookie);
        Assert.Contains("samesite=strict", cookie);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task TestAccounts_CannotBeCreatedOutsideTheTestingEnvironment(string environment)
    {
        await ClearUsersAsync();
        await using var other = new ProductionFactoryWithEnvironment(Factory.ConnectionString, environment);

        var ex = Assert.ThrowsAny<Exception>(() => other.Browser());

        Assert.Contains("only allowed in the 'Testing' environment", Everything(ex));
        await using var db = Factory.CreateDbContext();
        Assert.Equal(0, await db.Users.CountAsync()); // nothing was created
    }

    private sealed class ProductionFactoryWithEnvironment : WebApplicationFactory<Program>
    {
        private readonly string _connection;
        private readonly string _environment;

        public ProductionFactoryWithEnvironment(string connection, string environment)
        {
            _connection = connection;
            _environment = environment;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(_environment);
            builder.UseSetting("ConnectionStrings:TechStore", _connection);
            builder.UseSetting("Auth:TestAccounts:0:Email", "admin@test.local");
            builder.UseSetting("Auth:TestAccounts:0:Password", $"pw-{Guid.NewGuid():N}-{Guid.NewGuid():N}");
            builder.UseSetting("Auth:TestAccounts:0:Role", "Admin");
        }
    }
}
