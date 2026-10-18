using System.Net;
using Employee_Api.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Employee_Api.Tests;

/// <summary>
/// Companion to <see cref="RateLimitingCoverageTests"/>: exercises the
/// "unknown" (null-IP) rate-limit partition end-to-end via a startup filter
/// that nulls <c>RemoteIpAddress</c>. This covers the
/// <c>?? "unknown"</c> branch through the real pipeline.
/// Also locks the forwarded-headers fix: X-Forwarded-For must produce
/// distinct per-client buckets instead of collapsing behind the proxy IP.
/// </summary>
public class RateLimitingCoverageTests2
{
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

    private sealed class NullIpFactory : WebApplicationFactory<Program>
    {
        public string DbName { get; } = "RLNULL2_" + Guid.NewGuid().ToString("N");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("TESTING_DB_NAME", DbName);
            builder.ConfigureServices(s => s.AddTransient<IStartupFilter, NullIpStartupFilter>());
        }
    }

    private sealed class LowLimitFactory : WebApplicationFactory<Program>
    {
        public string DbName { get; } = "RLFWD2_" + Guid.NewGuid().ToString("N");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("TESTING_DB_NAME", DbName);
            builder.UseSetting("RateLimiting:PermitLimit", "1");
            builder.UseSetting("RateLimiting:WindowInSeconds", "60");
            builder.UseSetting("RateLimiting:QueueLimit", "0");
        }
    }

    private static void EnsureSeeded(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<EmployeeDbContext>();
        if (!ctx.Database.IsInMemory())
        {
            throw new InvalidOperationException(
                "SAFETY: refusing to seed/reset a non-InMemory database. " +
                $"Provider: {ctx.Database.ProviderName}.");
        }

        ctx.Database.EnsureDeleted();
        ctx.Database.EnsureCreated();
    }

    [Fact]
    public async Task NullIp_Partition_Allows_Request_EndToEnd()
    {
        using var factory = new NullIpFactory();
        EnsureSeeded(factory.Services);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/employees?pageNumber=1&pageSize=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task NullIp_Partition_Second_Request_Still_Served_Within_PermitLimit()
    {
        using var factory = new NullIpFactory();
        EnsureSeeded(factory.Services);
        using var client = factory.CreateClient();

        using var first = await client.GetAsync("/api/employees?pageNumber=1&pageSize=1");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var second = await client.GetAsync("/api/employees?pageNumber=1&pageSize=1");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    [Fact]
    public async Task ForwardedHeaders_Options_Are_Configured()
    {
        using var factory = new NullIpFactory();
        var options = factory.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
        Assert.True(options.ForwardedHeaders.HasFlag(ForwardedHeaders.XForwardedFor));
        Assert.True(options.ForwardedHeaders.HasFlag(ForwardedHeaders.XForwardedProto));
    }

    [Fact]
    public void Base_Config_PermitLimit_Is_Production_Safe()
    {
        // Guards the codzee finding: the base appsettings.json must not ship
        // a test-only 2 req/min limit. Development keeps its own strict
        // override in appsettings.Development.json.
        using var doc = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "appsettings.json")));
        var limit = doc.RootElement.GetProperty("RateLimiting").GetProperty("PermitLimit").GetInt32();
        Assert.True(limit >= 100, $"Base RateLimiting:PermitLimit is {limit}, expected >= 100 for production.");
    }

    [Fact]
    public async Task ForwardedFor_Different_Clients_Get_Distinct_Buckets()
    {
        // PermitLimit=1: without forwarded headers both requests would share
        // the TestServer IP bucket and the second would 429. With
        // UseForwardedHeaders each X-Forwarded-For IP gets its own bucket.
        using var factory = new LowLimitFactory();
        EnsureSeeded(factory.Services);
        using var client = factory.CreateClient();

        using var first = await SendWithForwardedForAsync(client, "203.0.113.10");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var second = await SendWithForwardedForAsync(client, "203.0.113.11");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    [Fact]
    public async Task ForwardedFor_Same_Client_Still_Limited()
    {
        // Same spoofed IP must still share one bucket -> second 429.
        using var factory = new LowLimitFactory();
        EnsureSeeded(factory.Services);
        using var client = factory.CreateClient();

        using var first = await SendWithForwardedForAsync(client, "203.0.113.20");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        HttpStatusCode? last = null;
        for (int i = 0; i < 5 && last != HttpStatusCode.TooManyRequests; i++)
        {
            using var r = await SendWithForwardedForAsync(client, "203.0.113.20");
            last = r.StatusCode;
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last);
    }

    private static Task<HttpResponseMessage> SendWithForwardedForAsync(HttpClient client, string ip)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/employees?pageNumber=1&pageSize=1");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", ip);
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        return client.SendAsync(request);
    }
}
