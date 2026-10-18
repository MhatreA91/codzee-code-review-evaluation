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

    private sealed class ForwardedHeadersFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbName = "RLFWDV_" + Guid.NewGuid().ToString("N");
        private readonly string[] _proxies;
        private readonly string[] _networks;

        public ForwardedHeadersFactory(string proxies, string networks)
            : this([proxies], [networks])
        {
        }

        public ForwardedHeadersFactory(string[] proxies, string[] networks)
        {
            _proxies = proxies;
            _networks = networks;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("TESTING_DB_NAME", _dbName);
            for (int i = 0; i < _proxies.Length; i++)
            {
                builder.UseSetting($"ForwardedHeaders:KnownProxies:{i}", _proxies[i]);
            }
            for (int i = 0; i < _networks.Length; i++)
            {
                builder.UseSetting($"ForwardedHeaders:KnownNetworks:{i}", _networks[i]);
            }
        }
    }

    [Fact]
    public void ForwardedHeaders_Invalid_Proxy_Fails_Startup_Loudly()
    {
        // A typo'd proxy IP must fail fast with a clear message, not silently
        // leave the real proxy untrusted (which would collapse every client
        // into one rate-limit bucket with nothing saying why).
        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            using var factory = new ForwardedHeadersFactory("not-an-ip", "10.0.0.0/8");
            factory.CreateClient();
        });
        Assert.Contains("ForwardedHeaders:KnownProxies", ex.Message);
    }

    [Fact]
    public void ForwardedHeaders_Invalid_Network_Fails_Startup_Loudly()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            using var factory = new ForwardedHeadersFactory("10.0.0.10", "not-a-cidr");
            factory.CreateClient();
        });
        Assert.Contains("ForwardedHeaders:KnownNetworks", ex.Message);
    }

    [Fact]
    public void ForwardedHeaders_Valid_Config_Reaches_Options()
    {
        // Happy path for the top-level parsing: a valid proxy IP and CIDR
        // must land in ForwardedHeadersOptions. If the copy loop broke,
        // every proxied client would share one rate-limit bucket.
        using var factory = new ForwardedHeadersFactory("10.0.0.10", "10.0.0.0/8");
        var options = factory.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
        Assert.Contains(System.Net.IPAddress.Parse("10.0.0.10"), options.KnownProxies);
#if NET10_0_OR_GREATER
        Assert.Contains(
            options.KnownIPNetworks,
            n => n.BaseAddress.Equals(System.Net.IPAddress.Parse("10.0.0.0")) && n.PrefixLength == 8);
#else
        Assert.Contains(
            options.KnownNetworks,
            n => n.Address.Equals(System.Net.IPAddress.Parse("10.0.0.0")) && n.PrefixLength == 8);
#endif
    }

    [Fact]
    public void ForwardedHeaders_Bare_Ip_In_KnownNetworks_Becomes_Single_Host_Network()
    {
        // A bare IP (no "/prefix") in KnownNetworks must become a /32 (IPv4)
        // or /128 (IPv6) single-host network rather than being dropped.
        using var factory = new ForwardedHeadersFactory([], ["192.168.1.50", "2001:db8::7"]);
        var options = factory.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
#if NET10_0_OR_GREATER
        Assert.Contains(
            options.KnownIPNetworks,
            n => n.BaseAddress.Equals(System.Net.IPAddress.Parse("192.168.1.50")) && n.PrefixLength == 32);
        Assert.Contains(
            options.KnownIPNetworks,
            n => n.BaseAddress.Equals(System.Net.IPAddress.Parse("2001:db8::7")) && n.PrefixLength == 128);
#else
        Assert.Contains(
            options.KnownNetworks,
            n => n.Address.Equals(System.Net.IPAddress.Parse("192.168.1.50")) && n.PrefixLength == 32);
        Assert.Contains(
            options.KnownNetworks,
            n => n.Address.Equals(System.Net.IPAddress.Parse("2001:db8::7")) && n.PrefixLength == 128);
#endif
    }

    [Fact]
    public async Task ForwardedHeaders_Defaults_Do_Not_Trust_Every_Proxy()
    {
        // Security lock: without configured KnownProxies/KnownNetworks the
        // defaults must still trust only loopback, so a direct client cannot
        // spoof X-Forwarded-For to dodge the per-IP rate limit.
        using var factory = new NullIpFactory();
        var options = factory.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
#if NET10_0_OR_GREATER
        Assert.DoesNotContain(
            options.KnownIPNetworks,
            n => n.PrefixLength == 0 && System.Net.IPAddress.Parse("0.0.0.0").Equals(n.BaseAddress));
#else
        Assert.Empty(options.KnownNetworks);
#endif
        Assert.DoesNotContain(
            options.KnownProxies,
            ip => ip.Equals(System.Net.IPAddress.Any) || ip.Equals(System.Net.IPAddress.IPv6Any));
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

    private static async Task<HttpResponseMessage> SendWithForwardedForAsync(HttpClient client, string ip)
    {
        // async + await: keeps the HttpRequestMessage alive until SendAsync
        // finishes. Returning the bare task from a `using var request` method
        // would dispose the request while the send is still in flight (race).
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/employees?pageNumber=1&pageSize=1");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", ip);
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        return await client.SendAsync(request);
    }
}
