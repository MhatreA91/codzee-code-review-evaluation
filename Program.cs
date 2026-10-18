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

// Validate + surface trusted-proxy config BEFORE the options callback: a
// typo in ForwardedHeaders config would otherwise leave the real proxy
// untrusted, collapsing every client into its single rate-limit bucket with
// nothing saying why. Fail fast at startup on the first malformed entry.
var knownProxies = new List<System.Net.IPAddress>();
foreach (var raw in builder.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [])
{
    var entry = raw?.Trim();
    if (string.IsNullOrEmpty(entry))
    {
        continue;
    }

    if (!System.Net.IPAddress.TryParse(entry, out var proxyIp))
    {
        throw new InvalidOperationException(
            $"Invalid ForwardedHeaders:KnownProxies entry '{raw}'. Expected an IP address (e.g. \"10.0.0.10\").");
    }

    knownProxies.Add(proxyIp);
}

#if NET10_0_OR_GREATER
var knownIpNetworks = new List<System.Net.IPNetwork>();
#else
var knownNetworks = new List<IPNetwork>();
#endif
foreach (var raw in builder.Configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [])
{
    var entry = raw?.Trim();
    if (string.IsNullOrEmpty(entry))
    {
        continue;
    }

    try
    {
#if NET10_0_OR_GREATER
        if (entry.Contains('/'))
        {
            knownIpNetworks.Add(System.Net.IPNetwork.Parse(entry));
        }
        else if (System.Net.IPAddress.TryParse(entry, out var netIp))
        {
            knownIpNetworks.Add(new System.Net.IPNetwork(
                netIp,
                netIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128));
        }
        else
        {
            throw new FormatException($"'{raw}' is not an IP address or CIDR range.");
        }
#else
        var parts = entry.Split('/', 2);
        if (!System.Net.IPAddress.TryParse(parts[0].Trim(), out var networkIp))
        {
            throw new FormatException($"'{raw}' is not an IP address or CIDR range.");
        }

        var prefix = parts.Length > 1 && int.TryParse(parts[1].Trim(), out var p)
            ? p
            : (networkIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
        knownNetworks.Add(new IPNetwork(networkIp, prefix));
#endif
    }
    catch (Exception ex) when (ex is FormatException || ex is ArgumentException || ex is OverflowException)
    {
        throw new InvalidOperationException(
            $"Invalid ForwardedHeaders:KnownNetworks entry '{raw}'. Expected a CIDR range (e.g. \"10.0.0.0/8\") or IP address.", ex);
    }
}

// Trust X-Forwarded-For / X-Forwarded-Proto ONLY from known proxies /
// networks so Connection.RemoteIpAddress reflects the real client IP.
// This keeps the per-IP rate-limit partition from collapsing all clients
// behind a proxy into a single bucket. The ASP.NET Core default trusts
// loopback proxies only — we KEEP that default and only ADD the validated
// proxies above. Never Clear() KnownProxies/KnownIPNetworks: clearing would
// trust X-Forwarded-* from any direct caller, letting a client rotate
// X-Forwarded-For to get a fresh rate-limit bucket per request (bypassing
// the limit) and spoof X-Forwarded-Proto:https to skip HTTPS redirection.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

    foreach (var proxyIp in knownProxies)
    {
        options.KnownProxies.Add(proxyIp);
    }

#if NET10_0_OR_GREATER
    foreach (var network in knownIpNetworks)
    {
        options.KnownIPNetworks.Add(network);
    }
#else
    foreach (var network in knownNetworks)
    {
        options.KnownNetworks.Add(network);
    }
#endif
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

