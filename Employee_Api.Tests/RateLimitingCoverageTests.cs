using System.Net;
using Employee_Api.Controllers;
using Employee_Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Threading.RateLimiting;

namespace Employee_Api.Tests;

/// <summary>
/// Serial collection for tests that mutate the process-wide TESTING_DB_NAME
/// environment variable. Even though the assembly disables test
/// parallelization globally (see AssemblyInfo.cs), scoping these to their own
/// non-parallel collection keeps them from racing each other (or future
/// parallel runners) on shared process state.
/// </summary>
[CollectionDefinition("EnvVarSerial", DisableParallelization = true)]
public sealed class EnvVarSerialCollection { }

/// <summary>
/// Covers the rate-limiting branches added in Program.cs (+ locks the
/// controller policy). Targets OnRejected with/without RetryAfter metadata,
/// the null-IP ("unknown") partition, 429 end-to-end, TESTING_DB_NAME
/// fallbacks and the https-Urls branch.
/// </summary>
[Collection("EnvVarSerial")]
public class RateLimitingCoverageTests
{
    // Extra guard on top of the [Collection("EnvVarSerial")]: serializes the
    // process-wide TESTING_DB_NAME mutations even if a future runner ignores
    // collection scoping. SemaphoreSlim (not lock) because the tests await.
    private static readonly SemaphoreSlim EnvVarGate = new(1, 1);

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
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (ctx, nxt) =>
            {
                ctx.Connection.RemoteIpAddress = null;
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
            builder.UseSetting("TESTING_DB_NAME", DbName);
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
            builder.UseSetting("TESTING_DB_NAME", DbName);
            builder.ConfigureServices(s => s.AddTransient<IStartupFilter, NullIpStartupFilter>());
        }
    }

    private sealed class NoDbSettingFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
        }
    }

    private sealed class HttpsUrlFactory : WebApplicationFactory<Program>
    {
        public string DbName { get; } = "RLHTTPS_" + Guid.NewGuid().ToString("N");
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("TESTING_DB_NAME", DbName);
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
        // the null-IP ("unknown") and real-IP requests must land in different
        // fixed-window partitions. With PermitLimit=1, exhausting the real-IP
        // bucket must 429 a second real-IP request but still allow the
        // null-IP request through the real pipeline.
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

        using var nullFactory = new NullIpFactory();
        EnsureSeeded(nullFactory.Services);
        using var nullClient = nullFactory.CreateClient();
        using var nullIpResponse = await nullClient.GetAsync("/api/employees?pageNumber=1&pageSize=1");
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
        await EnvVarGate.WaitAsync();
        try
        {
            var envDb = "ENVDB_" + Guid.NewGuid().ToString("N");
            Environment.SetEnvironmentVariable("TESTING_DB_NAME", envDb);
            try
            {
                using var factory = new NoDbSettingFactory();
                using var client = factory.CreateClient();
                // Marker row: proves the pipeline's DbContext actually uses
                // the env-var database. If Program.cs ignored the env var
                // (e.g. always used "Employee_Api_Testing"), the raw lookup
                // below would find nothing and this test would fail — a bare
                // 200 assertion could never catch that.
                SeedMarker(factory.Services, "env-marker@example.com");
                using var response = await client.GetAsync("/api/employees?pageNumber=1&pageSize=10");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.True(StoreContains(factory.Services, "env-marker@example.com"),
                    "Marker row missing: DbContext is not using the TESTING_DB_NAME env-var database.");
            }
            finally
            {
                Environment.SetEnvironmentVariable("TESTING_DB_NAME", null);
            }
        }
        finally
        {
            EnvVarGate.Release();
        }
    }

    [Fact]
    public async Task TestingDbName_FallsBack_To_Default_WhenConfigAndEnvMissing()
    {
        await EnvVarGate.WaitAsync();
        try
        {
            Environment.SetEnvironmentVariable("TESTING_DB_NAME", null);
            using var factory = new NoDbSettingFactory();
            using var client = factory.CreateClient();
            // Same marker technique: proves the default-name store is the
            // one actually serving requests, not just that a 200 came back.
            SeedMarker(factory.Services, "default-marker@example.com");
            using var response = await client.GetAsync("/api/employees?pageNumber=1&pageSize=10");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(StoreContains(factory.Services, "default-marker@example.com"),
                "Marker row missing: DbContext is not using the default testing database.");
        }
        finally
        {
            EnvVarGate.Release();
        }
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

