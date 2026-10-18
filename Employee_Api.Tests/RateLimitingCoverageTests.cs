using System.Net;
using System.Reflection;
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
/// Covers the rate-limiting branches added in Program.cs (+ locks the
/// controller policy). Targets OnRejected with/without RetryAfter metadata,
/// the null-IP ("unknown") partition, 429 end-to-end, TESTING_DB_NAME
/// fallbacks and the https-Urls branch.
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
    public void RateLimiter_Policy_ReturnsLimiter_ForNullAndNonNullIp()
    {
        using var factory = new TestApiFactory();
        EnsureSeeded(factory.Services);
        var options = factory.Services.GetRequiredService<IOptions<RateLimiterOptions>>().Value;

        // Policies are stored in the internal PolicyMap<string, DefaultRateLimiterPolicy>.
        var mapProp = typeof(RateLimiterOptions).GetProperty("PolicyMap", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(mapProp);
        var map = Assert.IsAssignableFrom<System.Collections.IDictionary>(mapProp!.GetValue(options));
        Assert.True(map.Contains("EmployeeApiPolicy"));
        var policy = map["EmployeeApiPolicy"]!;
        Assert.NotNull(policy);

        // DefaultRateLimiterPolicy wraps the Func<HttpContext, RateLimitPartition<DefaultKeyType>>
        // partitioner; invoke GetPartition for both IP shapes to cover "?? \"unknown\"" true/false.
        // (internal type: DefaultRateLimiterPolicy.GetPartition(HttpContext) is public.)
        var getPartition = policy.GetType().GetMethod("GetPartition", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(getPartition);

        var withIp = new DefaultHttpContext();
        withIp.Connection.RemoteIpAddress = IPAddress.Parse("127.0.0.1");
        var withoutIp = new DefaultHttpContext();
        withoutIp.Connection.RemoteIpAddress = null;

        Assert.NotNull(getPartition!.Invoke(policy, new object[] { withIp }));
        Assert.NotNull(getPartition.Invoke(policy, new object[] { withoutIp }));
    }

    [Fact]
    public async Task RateLimiter_Rejects_With429_AndBody_WhenPermitLimitExceeded()
    {
        using var factory = new LowLimitFactory();
        EnsureSeeded(factory.Services);
        using var client = factory.CreateClient();

        using var first = await client.GetAsync("/api/employees?pageNumber=1&pageSize=1");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        HttpStatusCode? last = null;
        string body = "";
        for (int i = 0; i < 10; i++)
        {
            using var r = await client.GetAsync("/api/employees?pageNumber=1&pageSize=1");
            last = r.StatusCode;
            body = await r.Content.ReadAsStringAsync();
            if (last == HttpStatusCode.TooManyRequests) break;
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last);
        Assert.Contains("Too many requests", body);
    }

    [Fact]
    public async Task TestingDbName_FallsBack_To_EnvironmentVariable_WhenConfigMissing()
    {
        var envDb = "ENVDB_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable("TESTING_DB_NAME", envDb);
        try
        {
            using var factory = new NoDbSettingFactory();
            using var client = factory.CreateClient();
            EnsureSeeded(factory.Services);
            using var response = await client.GetAsync("/api/employees?pageNumber=1&pageSize=1");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TESTING_DB_NAME", null);
        }
    }

    [Fact]
    public async Task TestingDbName_FallsBack_To_Default_WhenConfigAndEnvMissing()
    {
        Environment.SetEnvironmentVariable("TESTING_DB_NAME", null);
        using var factory = new NoDbSettingFactory();
        using var client = factory.CreateClient();
        EnsureSeeded(factory.Services);
        using var response = await client.GetAsync("/api/employees?pageNumber=1&pageSize=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Pipeline_Covers_HttpsUrls_Any_Branch()
    {
        using var factory = new HttpsUrlFactory();
        using var client = factory.CreateClient();
        EnsureSeeded(factory.Services);
        using var response = await client.GetAsync("/api/employees?pageNumber=1&pageSize=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var addresses = factory.Server.Features.Get<IServerAddressesFeature>();
        Assert.NotNull(addresses);
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
    public void RateLimiterOptions_Has_EmployeeApiPolicy_And429Rejection()
    {
        using var factory = new TestApiFactory();
        var options = factory.Services.GetRequiredService<IOptions<RateLimiterOptions>>().Value;
        Assert.Equal(StatusCodes.Status429TooManyRequests, options.RejectionStatusCode);
        var mapProp = typeof(RateLimiterOptions).GetProperty("PolicyMap", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(mapProp);
        var map = Assert.IsAssignableFrom<System.Collections.IDictionary>(mapProp!.GetValue(options));
        Assert.True(map.Contains("EmployeeApiPolicy"));
    }
}

