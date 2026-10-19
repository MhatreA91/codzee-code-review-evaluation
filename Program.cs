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
// appsettings.Development.json keeps a strict 100 req/min for local testing.
var permitLimit = builder.Configuration.GetValue<int>("RateLimiting:PermitLimit", 100);
var windowInSeconds = builder.Configuration.GetValue<int>("RateLimiting:WindowInSeconds", 60);
var queueLimit = builder.Configuration.GetValue<int>("RateLimiting:QueueLimit", 0);

// Fail fast: FixedWindowRateLimiterOptions throws ArgumentOutOfRangeException
// for non-positive PermitLimit/Window (and negative QueueLimit), and the
// limiter's internal replenish Timer cannot exceed ~49.7 days — both would
// surface as a 500 on first request. Validate here with a clear message.
if (permitLimit < 1)
{
    throw new InvalidOperationException(
        $"Invalid RateLimiting:PermitLimit '{permitLimit}'. Expected an integer >= 1.");
}
if (windowInSeconds < 1 || windowInSeconds > 86_400)
{
    throw new InvalidOperationException(
        $"Invalid RateLimiting:WindowInSeconds '{windowInSeconds}'. Expected an integer between 1 and 86400 (up to 24 hours).");
}
if (queueLimit < 0)
{
    throw new InvalidOperationException(
        $"Invalid RateLimiting:QueueLimit '{queueLimit}'. Expected an integer >= 0.");
}

builder.Services.AddControllers();
// Swagger generator (Swashbuckle) — single docs source.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// EF Core: SQL Server via connection string in appsettings.json (no hardcoded credentials).
// NOTE: there is intentionally no "Testing"-environment branch here. The app always
// uses the production provider; integration tests override EmployeeDbContext to an
// isolated InMemory database inside TestApiFactory (test project only), so a stray
// ASPNETCORE_ENVIRONMENT=Testing can never silently swap a deployed app to InMemory.
builder.Services.AddDbContext<EmployeeDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

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

        var maxPrefix = networkIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        int prefix;
        if (parts.Length == 1)
        {
            // Bare IP — /32 (IPv4) or /128 (IPv6) single host.
            prefix = maxPrefix;
        }
        else if (!int.TryParse(parts[1].Trim(), out prefix) || prefix < 0 || prefix > maxPrefix)
        {
            // Malformed ("10.0.0.0/abc") or out-of-range ("10.0.0.0/33")
            // prefixes must fail fast, not silently become a single host.
            throw new FormatException($"'{raw}' has an invalid CIDR prefix. Expected 0-{maxPrefix}.");
        }
        knownNetworks.Add(new IPNetwork(networkIp, prefix));
#endif
    }
    catch (Exception ex) when (ex is FormatException || ex is ArgumentException || ex is OverflowException)
    {
        throw new InvalidOperationException(
            $"Invalid ForwardedHeaders:KnownNetworks entry '{raw}'. Expected a CIDR range (e.g. \"10.0.0.0/8\") or IP address.", ex);
    }
}

// Trust X-Forwarded-For ONLY from known proxies /
// networks so Connection.RemoteIpAddress reflects the real client IP.
// This keeps the per-IP rate-limit partition from collapsing all clients
// behind a proxy into a single bucket. The ASP.NET Core default trusts
// loopback proxies only — we KEEP that default and only ADD the validated
// proxies above. Never Clear() KnownProxies/KnownIPNetworks: clearing would
// trust X-Forwarded-For from any direct caller, letting a client rotate
// the header to get a fresh rate-limit bucket per request (bypassing
// the limit).
// NOTE: X-Forwarded-Proto is deliberately NOT enabled. It would rewrite
// Request.Scheme globally and change HTTPS-redirection behaviour for the
// whole app — outside this rate-limiting change's scope. Only the client
// IP is needed for per-IP partitioning.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor;

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

        context.HttpContext.Response.ContentType = "text/plain; charset=utf-8";
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

// Must run first: rewrites RemoteIpAddress from X-Forwarded-For
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

// Rate limiting runs BEFORE authorization so abusive callers burn budget
// even when they would fail auth. (Per-IP fixed-window policy; see
// AddRateLimiter above and [EnableRateLimiting] on the controller.)
app.UseRateLimiter();

app.UseAuthorization();

app.MapControllers();

app.Run();

/// <summary>
/// Testability hook only — exposes the top-level-statements entry point to
/// <c>WebApplicationFactory&lt;Program&gt;</c>. No runtime behavior change.
/// </summary>
public partial class Program { }

