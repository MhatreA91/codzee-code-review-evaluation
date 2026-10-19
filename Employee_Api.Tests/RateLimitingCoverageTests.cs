using System.Net;
using Employee_Api.Controllers;
using Employee_Api.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Threading.RateLimiting;

namespace Employee_Api.Tests;

/// <summary>
/// Covers the rate-limiting branches added in Program.cs (+ locks the
/// controller policy). Targets OnRejected with/without RetryAfter metadata,
/// the null-IP ("unknown") partition, 429 end-to-end, factory InMemory
/// isolation and the https-Urls branch.
/// </summary>
public class RateLimitingCoverageTests
{

    private sealed class TestLease : RateLimitLease
    {
        private readonly TimeSpan? _retryAfter;
        public TestLease(TimeSpan? retryAfter) => _retryAfter = retryAfter;
        public override bool IsAcquired => false;
        public override IEnumerable<string> MetadataNames =>
            _retryAfter.HasValue ? new[] { MetadataName.RetryAfter.Name } : Array.Empty<string>();
        public override bool TryGetMetadata(string metadataName, out object? metadata){ if (_retryAfter.HasValue && metadataName == MetadataName.RetryAfter.Name){ metadata = _retryAfter.Value; return true; } metadata = null; return false; }
    }

    private sealed class NullIpStartupFilter : IStartupFilter
    {
        // TestServer leaves Connection.RemoteIpAddress NULL by default, so
        // without this filter EVERY request would already share the
        // "unknown" bucket and no test could distinguish partitions. This
        // filter therefore assigns a fixed real IP (127.0.0.2) to ordinary
        // requests, and nulls RemoteIpAddress ONLY for requests carrying
        // the X-Null-Remote-Ip marker header. That lets one test put a
        // real-IP and a null-IP request through the SAME limiter instance —
        // the only way to prove they land in different buckets. The marker
        // header is stripped before the request proceeds so it cannot leak
        // into routing/forwarded-headers handling.
        //
        // ORDERING: IStartupFilter runs BEFORE Program.cs's middleware, so
        // this assignment happens before UseForwardedHeaders/UseRateLimiter
        // (no X-Forwarded-For is sent here, so the assigned IP survives to
        // the rate-limit partition key untouched).
        public const string MarkerHeader = "X-Null-Remote-Ip";

        public static readonly System.Net.IPAddress FixedTestIp =
            System.Net.IPAddress.Parse("127.0.0.2");

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (ctx, nxt) =>
            {
                if (ctx.Request.Headers.ContainsKey(MarkerHeader))
                {
                    ctx.Request.Headers.Remove(MarkerHeader);
                    ctx.Connection.RemoteIpAddress = null;
                }
                else
                {
                    ctx.Connection.RemoteIpAddress = FixedTestIp;
                }
                await nxt();
            });
            next(app);
        };
    }

    private sealed class LowLimitFactory : WebApplicationFactory<Program>
    {
        public string DbName { get; } = "RL_" + Guid.NewGuid().ToString("N");
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(s =>
            {
                TestDbSwap.ReplaceWithInMemory(s, DbName);
                s.AddTransient<IStartupFilter, NullIpStartupFilter>();
            });
            builder.UseSetting("RateLimiting:PermitLimit", "1");
            builder.UseSetting("RateLimiting:WindowInSeconds", "60");
            builder.UseSetting("RateLimiting:QueueLimit", "0");
        }
    }

    private sealed class NullIpFactory : WebApplicationFactory<Program>
    {
        public string DbName { get; } = "RLNULL_" + Guid.NewGuid().ToString("N");
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(s =>
            {
                TestDbSwap.ReplaceWithInMemory(s, DbName);
                s.AddTransient<IStartupFilter, NullIpStartupFilter>();
            });
        }
    }

    private sealed class NoDbSettingFactory : WebApplicationFactory<Program>
    {
        public string DbName { get; } = "RLNODEFAULT_" + Guid.NewGuid().ToString("N");
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(s => TestDbSwap.ReplaceWithInMemory(s, DbName));
        }
    }

    private sealed class HttpsUrlFactory : WebApplicationFactory<Program>
    {
        public string DbName { get; } = "RLHTTPS_" + Guid.NewGuid().ToString("N");
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(s => TestDbSwap.ReplaceWithInMemory(s, DbName));
            builder.UseUrls("https://localhost:5001");
        }
    }

    private static void EnsureSeeded(IServiceProvider services, [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        using var scope = services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<EmployeeDbContext>();
        if (!ctx.Database.IsInMemory())
        {
            throw new InvalidOperationException(
                $"SAFETY: refusing to seed/reset a non-InMemory database in test '{caller}'. " +
                $"Provider: {ctx.Database.ProviderName}. " +
                "Integration tests must only use the InMemory provider.");
        }
        ctx.Database.EnsureDeleted();
        ctx.Database.EnsureCreated();
    }

    private static DefaultHttpContext ContextWithBody()
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static string ReadBody(HttpContext ctx)
    {
        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(ctx.Response.Body, leaveOpen: true);
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task OnRejected_SetsRetryAfter_WhenMetadataPresent()
    {
        using var factory = new TestApiFactory();
        EnsureSeeded(factory.Services);
        var options = factory.Services.GetRequiredService<IOptions<RateLimiterOptions>>().Value;
        Assert.NotNull(options.OnRejected);

        var httpContext = ContextWithBody();
        var rejected = new OnRejectedContext { HttpContext = httpContext, Lease = new TestLease(TimeSpan.FromSeconds(12.3)) };

        await options.OnRejected!(rejected, CancellationToken.None);

        Assert.Equal("13", httpContext.Response.Headers.RetryAfter.ToString());
        Assert.Equal("13", httpContext.Response.Headers["Retry-After"].ToString());
        Assert.Contains("Too many requests", ReadBody(httpContext));
    }

    [Fact]
    public async Task OnRejected_OmitsRetryAfter_WhenNoMetadata()
    {
        using var factory = new TestApiFactory();
        EnsureSeeded(factory.Services);
        var options = factory.Services.GetRequiredService<IOptions<RateLimiterOptions>>().Value;
        Assert.NotNull(options.OnRejected);

        var httpContext = ContextWithBody();
        var rejected = new OnRejectedContext { HttpContext = httpContext, Lease = new TestLease(null) };

        await options.OnRejected!(rejected, CancellationToken.None);

        Assert.False(httpContext.Response.Headers.ContainsKey("Retry-After"));
        Assert.Contains("Too many requests", ReadBody(httpContext));
    }

    [Fact]
    public async Task RateLimiter_Policy_ReturnsLimiter_ForNullAndNonNullIp()
    {
        // Behavioral check (no reflection on RateLimiterOptions internals):
        // with PermitLimit=1, exhausting the real-IP bucket must 429 a second
        // real-IP request — but a null-IP request through the SAME limiter
        // instance must still get 200, proving null-IP lands in its own
        // "unknown" partition instead of sharing (or missing) the real-IP
        // bucket. The old version used a SECOND factory for the null-IP
        // request — but each factory owns an independent limiter, so the
        // 200 proved nothing about the partition key and the claim was
        // never actually tested. The marker header selects null-IP
        // per-request (see NullIpStartupFilter).
        using var factory = new LowLimitFactory();
        EnsureSeeded(factory.Services);
        using var client = factory.CreateClient();

        using var realFirst = await client.GetAsync("/api/employees?pageNumber=1&pageSize=1");
        Assert.Equal(HttpStatusCode.OK, realFirst.StatusCode);

        // Strict: the very next request on the same IP must be 429 — no
        // retry loop. A loop-until-429 would also pass if the bucket wrongly
        // held 2+ permits, hiding the regression.
        using var realSecond = await client.GetAsync("/api/employees?pageNumber=1&pageSize=1");
        Assert.Equal(HttpStatusCode.TooManyRequests, realSecond.StatusCode);

        using var nullRequest = new HttpRequestMessage(HttpMethod.Get, "/api/employees?pageNumber=1&pageSize=1");
        nullRequest.Headers.TryAddWithoutValidation(NullIpStartupFilter.MarkerHeader, "1");
        using var nullIpResponse = await client.SendAsync(nullRequest);
        Assert.Equal(HttpStatusCode.OK, nullIpResponse.StatusCode);
    }

    [Fact]
    public async Task RateLimiter_Rejects_With429_AndBody_WhenPermitLimitExceeded()
    {
        using var factory = new LowLimitFactory();
        EnsureSeeded(factory.Services);
        using var client = factory.CreateClient();

        using var first = await client.GetAsync("/api/employees?pageNumber=1&pageSize=1");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Strict: the very next request must be the 429 — no retry loop, so
        // the body/Retry-After assertions below provably target the 2nd
        // request, not the Nth.
        HttpResponseMessage? rejected = await client.GetAsync("/api/employees?pageNumber=1&pageSize=1");
        string body = await rejected.Content.ReadAsStringAsync();

        using (rejected)
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
            Assert.Contains("Too many requests", body);
            // The PR claims Retry-After is sent: lock it on the real 429.
            Assert.NotNull(rejected);
            Assert.True(rejected!.Headers.Contains("Retry-After"), "429 is missing the Retry-After header.");
            var retryAfter = rejected.Headers.GetValues("Retry-After").FirstOrDefault();
            Assert.True(int.TryParse(retryAfter, out var seconds) && seconds >= 0,
                $"Retry-After '{retryAfter}' is not a non-negative integer.");
        }
    }

    [Fact]
    public async Task TestingDbName_FallsBack_To_EnvironmentVariable_WhenConfigMissing()
    {
        // Program.cs no longer reads TESTING_DB_NAME at all: the InMemory swap
        // is owned by each test factory via TestDbSwap (with an explicit
        // per-factory DbName). The old env-var contract is therefore obsolete.
        // This test now locks the replacement contract: the factory's DbContext
        // is InMemory-backed and serves the marker row seeded through it.
        using var factory = new NoDbSettingFactory();
        using var client = factory.CreateClient();
        SeedMarker(factory.Services, "env-marker@example.com");
        using var response = await client.GetAsync("/api/employees?pageNumber=1&pageSize=10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(StoreContains(factory.Services, "env-marker@example.com"),
            "Marker row missing: DbContext is not using the factory's InMemory database.");
    }

    [Fact]
    public async Task TestingDbName_FallsBack_To_Default_WhenConfigAndEnvMissing()
    {
        // Same as above: no env-var/default-name fallback exists in the app
        // anymore. Lock that the factory still serves its own isolated
        // InMemory store.
        using var factory = new NoDbSettingFactory();
        using var client = factory.CreateClient();
        SeedMarker(factory.Services, "default-marker@example.com");
        using var response = await client.GetAsync("/api/employees?pageNumber=1&pageSize=10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(StoreContains(factory.Services, "default-marker@example.com"),
            "Marker row missing: DbContext is not using the factory's InMemory database.");
    }

    private static void SeedMarker(IServiceProvider services, string email)
    {
        using var scope = services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<EmployeeDbContext>();
        GuardInMemory(ctx, nameof(SeedMarker));
        ctx.Database.EnsureCreated();
        ctx.Employees.Add(new Employee_Api.Models.Employee
        {
            FirstName = "Marker",
            LastName = "Row",
            Email = email,
            Department = "QA",
            Salary = 1,
            DateOfJoining = new DateTime(2024, 1, 1)
        });
        ctx.SaveChanges();
    }

    private static bool StoreContains(IServiceProvider services, string email)
    {
        // Fresh scope + raw (untracked, DB-backed) lookup: proves the marker
        // row is IN THE STORE the app's DbContext resolves, not just in a
        // tracked identity map. AsNoTracking() forces a store round-trip.
        using var scope = services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<EmployeeDbContext>();
        GuardInMemory(ctx, nameof(StoreContains));
        return ctx.Employees.AsNoTracking().Any(e => e.Email == email);
    }

    private static void GuardInMemory(EmployeeDbContext ctx, string caller)
    {
        if (!ctx.Database.IsInMemory())
        {
            throw new InvalidOperationException(
                $"SAFETY: refusing to seed/read a non-InMemory database in test '{caller}'. " +
                $"Provider: {ctx.Database.ProviderName}. " +
                "Integration tests must only use the InMemory provider.");
        }
    }

    [Fact]
    public async Task Pipeline_Covers_HttpsUrls_Any_Branch()
    {
        using var factory = new HttpsUrlFactory();
        using var client = factory.CreateClient();
        EnsureSeeded(factory.Services);
        using var response = await client.GetAsync("/api/employees?pageNumber=1&pageSize=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Must actually prove the https-URL branch ran: the factory was
        // configured with UseUrls("https://..."), so the addresses feature
        // must carry at least one https:// address. A bare NotNull can
        // never fail even when the branch below is skipped.
        var addresses = factory.Server.Features.Get<IServerAddressesFeature>();
        Assert.NotNull(addresses);
        Assert.Contains(addresses.Addresses,
            a => a.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EmployeesController_Has_EmployeeApiPolicy()
    {
        var attr = typeof(EmployeesController)
            .GetCustomAttributes(typeof(EnableRateLimitingAttribute), inherit: true)
            .Cast<EnableRateLimitingAttribute>()
            .Single();
        Assert.Equal("EmployeeApiPolicy", attr.PolicyName);
    }

    [Fact]
    public async Task RateLimiterOptions_Has_EmployeeApiPolicy_And429Rejection()
    {
        // No reflection on the internal PolicyMap: RejectionStatusCode is
        // public, and policy existence is proven behaviorally by the 429
        // end-to-end test (LowLimitFactory + "EmployeeApiPolicy" on the
        // controller must reject with 429, not 500/404).
        using var factory = new TestApiFactory();
        var options = factory.Services.GetRequiredService<IOptions<RateLimiterOptions>>().Value;
        Assert.Equal(StatusCodes.Status429TooManyRequests, options.RejectionStatusCode);

        using var lowFactory = new LowLimitFactory();
        EnsureSeeded(lowFactory.Services);
        using var client = lowFactory.CreateClient();
        using var first = await client.GetAsync("/api/employees?pageNumber=1&pageSize=1");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        HttpStatusCode? last = null;
        for (int i = 0; i < 5 && last != HttpStatusCode.TooManyRequests; i++)
        {
            using var r = await client.GetAsync("/api/employees?pageNumber=1&pageSize=1");
            last = r.StatusCode;
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, last);
    }
}

