using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TechStore.Api.HealthChecks;
using TechStore.Api.Middleware;
using TechStore.Auth;
using TechStore.Database;
using TechStore.Services;

var builder = WebApplication.CreateBuilder(args);

// Connection string comes from appsettings.json, or the ConnectionStrings__TechStore environment variable.
var connectionString = builder.Configuration.GetConnectionString("TechStore")
    ?? throw new InvalidOperationException("Connection string 'TechStore' is not configured.");

// One DbContext per web request (scoped), so each request has its own change tracker and transaction.
builder.Services.AddDbContext<TechStoreDbContext>(options => options.UseNpgsql(connectionString));

// Services are scoped as well: they share the request's DbContext.
builder.Services.AddScoped<ProductService>();
builder.Services.AddScoped<ReferenceDataService>();
builder.Services.AddScoped<InventoryService>();
builder.Services.AddScoped<OrderService>();
builder.Services.AddScoped<ReservationService>();
builder.Services.AddScoped<FulfillmentService>();
builder.Services.AddScoped<DashboardService>();

// Authentication and authorization: Identity users in PostgreSQL, a cookie session, four capability policies, CSRF protection.
builder.Services.AddTechStoreAuth(builder.Configuration, builder.Environment);

builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database");

builder.Services.AddControllers()
    .ConfigureApiBehaviorOptions(options =>
    {
        // Requests that fail model binding (malformed JSON, missing required properties) get the same
        // 400 shape as validation failures raised by the services.
        options.InvalidModelStateResponseFactory = context => new BadRequestObjectResult(
            new ValidationProblemDetails(context.ModelState)
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Validation failed",
                Detail = "One or more fields are invalid."
            })
        {
            ContentTypes = { "application/problem+json" }
        };
    });

var app = builder.Build();

// Make sure the roles and the first Admin exist. If no Admin exists and none is configured, this stops the API from starting.
await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<AuthInitializer>().InitializeAsync();
}

app.UseForwardedHeaders();                          // does nothing unless Auth:TrustedProxies is set
app.UseMiddleware<SecurityHeadersMiddleware>();   // first, so every response gets the headers
app.UseRateLimiter();                              // limits sign-in attempts (429)
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseAuthentication(); // who is calling? (reads the session cookie)
app.UseAuthorization();  // may they do this? (checks the endpoint's policy)
app.MapControllers();
app.MapHealthChecks("/health").AllowAnonymous(); // the one open endpoint besides login and the CSRF token

app.Run();

// Makes the entry point visible to the integration tests (WebApplicationFactory<Program>).
public partial class Program
{
}
