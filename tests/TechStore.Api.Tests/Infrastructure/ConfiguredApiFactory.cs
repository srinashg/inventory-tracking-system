using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace TechStore.Api.Tests.Infrastructure;

/// <summary>
/// An API started with exactly the settings (and environment) a test names, against the shared test database, which already
/// holds the users. It has no test accounts, so it can run in "Production" to test deployment-only rules. A test can also set
/// the connection's remote address with the X-Test-Remote-Ip header, which stands in for the address of the machine (or proxy)
/// that opened the connection; a real client cannot choose that address.
/// </summary>
public class ConfiguredApiFactory : WebApplicationFactory<Program>
{
    public const string RemoteAddressHeader = "X-Test-Remote-Ip";

    private readonly string _environment;
    private readonly string _connectionString;
    private readonly Dictionary<string, string?> _settings;

    public ConfiguredApiFactory(string connectionString, string environment, Dictionary<string, string?>? settings = null)
    {
        _connectionString = connectionString;
        _environment = environment;
        _settings = settings ?? new Dictionary<string, string?>();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.UseSetting("ConnectionStrings:TechStore", _connectionString);
        foreach (var (key, value) in _settings) builder.UseSetting(key, value);

        builder.ConfigureServices(services => services.AddTransient<IStartupFilter, RemoteAddressStartupFilter>());
    }

    /// <summary>Runs first in the pipeline and sets Connection.RemoteIpAddress from the test header.</summary>
    private sealed class RemoteAddressStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use((context, nextMiddleware) =>
                {
                    if (context.Request.Headers.TryGetValue(RemoteAddressHeader, out var address) && System.Net.IPAddress.TryParse(address, out var ip))
                    {
                        context.Connection.RemoteIpAddress = ip;
                    }

                    return nextMiddleware(context);
                });
                next(app);
            };
    }
}
