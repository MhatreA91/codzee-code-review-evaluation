using Employee_Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Employee_Api.Tests;

public sealed class TestApiFactory : WebApplicationFactory<Program>
{
    private readonly string _environment;

    public TestApiFactory(string environment = "Testing") => _environment = environment;

    public string DbName { get; } = Guid.NewGuid().ToString();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.UseSetting("TESTING_DB_NAME", DbName);

        // SAFETY: never let integration tests resolve a SQL Server DbContext,
        // no matter which environment the factory emulates (Development /
        // Production tests previously hit the real database and
        // EnsureDeleted() wiped it). Remove the app's EF registration
        // entirely (including any cached internal provider) and force an
        // isolated InMemory store keyed by this factory's unique DbName.
        builder.ConfigureServices(services =>
        {
            for (int i = services.Count - 1; i >= 0; i--)
            {
                var t = services[i].ServiceType;
                if (t == typeof(DbContextOptions<EmployeeDbContext>)
                    || t == typeof(EmployeeDbContext)
                    || (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IDbContextOptionsConfiguration<>)))
                {
                    services.RemoveAt(i);
                }
            }

            services.AddDbContext<EmployeeDbContext>(options =>
                options.UseInMemoryDatabase(DbName));
        });
    }
}
