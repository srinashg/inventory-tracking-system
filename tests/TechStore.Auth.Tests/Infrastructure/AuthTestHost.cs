using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TechStore.Auth;
using TechStore.Database;
using TechStore.Database.Entities;

namespace TechStore.Auth.Tests.Infrastructure;

/// <summary>A minimal host environment so tests can pretend to run as "Testing", "Development" or "Production".</summary>
public sealed class FakeHostEnvironment : IHostEnvironment
{
    public FakeHostEnvironment(string environmentName) => EnvironmentName = environmentName;

    public string EnvironmentName { get; set; }
    public string ApplicationName { get; set; } = "TechStore.Auth.Tests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

/// <summary>Remembers every log message, so tests can prove that no password was ever logged.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<string> _messages = new();

    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (_messages) return _messages.ToList();
        }
    }

    public void Clear()
    {
        lock (_messages) _messages.Clear();
    }

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _messages);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly string _category;
        private readonly List<string> _messages;

        public CapturingLogger(string category, List<string> messages)
        {
            _category = category;
            _messages = messages;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var text = $"[{logLevel}] {_category}: {formatter(state, exception)}";
            if (exception is not null) text += " " + exception;
            lock (_messages) _messages.Add(text);
        }
    }
}

/// <summary>
/// A dependency-injection container built the way the real API builds it (AddTechStoreAuth), over the auth test database.
/// </summary>
public sealed class AuthTestHost : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    private AuthTestHost(ServiceProvider provider, CapturingLoggerProvider logs)
    {
        _provider = provider;
        Logs = logs;
    }

    public CapturingLoggerProvider Logs { get; }

    public static AuthTestHost Create(
        AuthDatabaseFixture database, string environment, Dictionary<string, string?>? settings = null, string? connectionString = null,
        Action<IServiceCollection>? configure = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings ?? new()).Build();
        var logs = new CapturingLoggerProvider();

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        services.AddDbContext<TechStoreDbContext>(options => options.UseNpgsql(connectionString ?? database.ConnectionString));
        services.AddSingleton<IHostEnvironment>(new FakeHostEnvironment(environment));
        services.AddSingleton<IConfiguration>(configuration);
        services.AddTechStoreAuth(configuration, new FakeHostEnvironment(environment));
        configure?.Invoke(services); // lets a test replace a service, after the real ones are registered

        return new AuthTestHost(services.BuildServiceProvider(), logs);
    }

    /// <summary>A new scope, like one web request.</summary>
    public AsyncServiceScope Scope() => _provider.CreateAsyncScope();

    public async Task InitializeAsync()
    {
        await using var scope = Scope();
        await scope.ServiceProvider.GetRequiredService<AuthInitializer>().InitializeAsync();
    }

    public async Task<AppUser?> FindUserAsync(string email)
    {
        await using var scope = Scope();
        return await scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>().FindByEmailAsync(email);
    }

    public async Task<IList<string>> RolesOfAsync(string email)
    {
        await using var scope = Scope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        return await users.GetRolesAsync((await users.FindByEmailAsync(email))!);
    }

    public async Task<int> UserCountAsync()
    {
        await using var scope = Scope();
        return await scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>().Users.CountAsync();
    }

    public async Task<bool> PasswordIsAsync(string email, string password)
    {
        await using var scope = Scope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        return await users.CheckPasswordAsync((await users.FindByEmailAsync(email))!, password);
    }

    public ValueTask DisposeAsync() => _provider.DisposeAsync();
}

/// <summary>Empties the authentication tables before every test.</summary>
[Collection(AuthDatabaseCollection.Name)]
public abstract class AuthTestBase : IAsyncLifetime
{
    protected AuthTestBase(AuthDatabaseFixture database)
    {
        Database = database;
    }

    protected AuthDatabaseFixture Database { get; }

    public async Task InitializeAsync()
    {
        await using var db = Database.CreateContext();
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE user_roles, user_claims, user_logins, user_tokens, role_claims, users, roles RESTART IDENTITY CASCADE");
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
