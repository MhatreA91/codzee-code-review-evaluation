using System.Net;
using Employee_Api.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Employee_Api.Tests;

/// <summary>
/// Companion to <see cref="RateLimitingCoverageTests"/>: exercises the
/// "unknown" (null-IP) rate-limit partition end-to-end via a startup filter
/// that nulls <c>RemoteIpAddress</c>. This covers the
/// <c>?? "unknown"</c> branch through the real pipeline.
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
}
