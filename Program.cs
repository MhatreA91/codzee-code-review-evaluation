using Employee_Api.Data;
using Employee_Api.Repositories;
using Employee_Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using System.Globalization;
using System.Threading.RateLimiting;
var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Defaults are production-safe (100 req/min/IP); Development override in
// appsettings.Development.json keeps a strict 2 req/min for local testing.
var permitLimit = builder.Configuration.GetValue<int>("RateLimiting:PermitLimit", 100);
var windowInSeconds = builder.Configuration.GetValue<int>("RateLimiting:WindowInSeconds", 60);
var queueLimit = builder.Configuration.GetValue<int>("RateLimiting:QueueLimit", 0);

builder.Services.AddControllers();
// Swagger generator (Swashbuckle) — single docs source.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// EF Core: SQL Server via connection string in appsettings.json (no hardcoded credentials).
// The Testing environment swaps in the InMemory provider for WebApplicationFactory integration tests.
// TESTING_DB_NAME gives each factory a unique isolated database.
if (builder.Environment.IsEnvironment("Testing"))
{
    var testDb = builder.Configuration.GetValue<string>("TESTING_DB_NAME")
        ?? Environment.GetEnvironmentVariable("TESTING_DB_NAME")
        ?? "Employee_Api_Testing";
    builder.Services.AddDbContext<EmployeeDbContext>(options =>
        options.UseInMemoryDatabase(testDb));
}
else
{
    builder.Services.AddDbContext<EmployeeDbContext>(options =>
        options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));
}

// Repository + Service layer DI
builder.Services.AddScoped<IEmployeeRepository, EmployeeRepository>();
builder.Services.AddScoped<IEmployeeService, EmployeeService>();

// Trust X-Forwarded-For / X-Forwarded-Proto from reverse proxies / load
// balancers so Connection.RemoteIpAddress reflects the real client IP.
// This keeps the per-IP rate-limit partition from collapsing all clients
// behind a proxy into a single bucket. KnownNetworks/KnownProxies are
// cleared so any upstream proxy is honored (typical for container / LB
// deployments); if the app is directly internet-facing, restrict these to
// your proxy IPs instead to avoid spoofed X-Forwarded-For headers.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
#if NET10_0_OR_GREATER
    options.KnownIPNetworks.Clear();
#else
    options.KnownNetworks.Clear();
#endif
    options.KnownProxies.Clear();
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.OnRejected = async (context, cancellationToken) =>
    {
        if (context.Lease.TryGetMetadata(
            MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                Math.Ceiling(retryAfter.TotalSeconds)
                    .ToString(CultureInfo.InvariantCulture);
        }

        await context.HttpContext.Response.WriteAsync(
            "Too many requests. Please try again later.",
            cancellationToken);
    };

    options.AddPolicy("EmployeeApiPolicy", httpContext =>
    {
        var clientIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(clientIp, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = TimeSpan.FromSeconds(windowInSeconds),            
            QueueLimit = queueLimit,
            AutoReplenishment = true
    }); 
    });
});

var app = builder.Build();

// Must run first: rewrites RemoteIpAddress/Scheme from X-Forwarded-*
// before auth, rate limiting, and routing see the request. Otherwise the
// rate-limit partition still sees the proxy IP.
app.UseForwardedHeaders();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Convenience: opening http://localhost:5174/ lands on the Swagger UI instead of 404.
app.MapGet("/", () => Results.Redirect("/swagger"));

// Only redirect HTTP -> HTTPS when an HTTPS endpoint is configured.
// The plain "http" launch profile has no HTTPS port, so this avoids the
// "Failed to determine the https port for redirect" warning.
var httpsPort = builder.Configuration.GetValue<int?>("ASPNETCORE_HTTPS_PORT");
if (app.Urls.Any(u => u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) || httpsPort.HasValue)
{
    app.UseHttpsRedirection();
}

app.UseAuthorization();

app.UseRateLimiter();

app.MapControllers();

app.Run();

/// <summary>
/// Testability hook only — exposes the top-level-statements entry point to
/// <c>WebApplicationFactory&lt;Program&gt;</c>. No runtime behavior change.
/// </summary>
public partial class Program { }

