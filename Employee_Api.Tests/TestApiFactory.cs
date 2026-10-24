using Employee_Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Employee_Api.Tests;

/// <summary>
/// Shared helper: replaces the SQL Server <see cref="EmployeeDbContext"/>
/// registration (which Program.cs now always registers) with an isolated
/// InMemory database. Used by every integration-test factory, including the
/// private factories in the rate-limiting test classes.
/// </summary>
public static class TestDbSwap
{
    public static void ReplaceWithInMemory(IServiceCollection services, string dbName)
    {
        // Remove every EF Core registration (options + context + provider
        // singletons such as the SqlServer internal provider); removing only
        // DbContextOptions leaves the SqlServer provider cached and EF throws
        // "multiple database providers".
        bool IsEf(ServiceDescriptor d) =>
            d.ServiceType.FullName?.Contains("EntityFrameworkCore") == true
            || d.ImplementationType?.FullName?.Contains("EntityFrameworkCore") == true
            || d.ImplementationInstance?.GetType().FullName?.Contains("EntityFrameworkCore") == true
            || d.ServiceType == typeof(DbContextOptions)
            || d.ServiceType == typeof(DbContextOptions<EmployeeDbContext>)
            || d.ServiceType == typeof(EmployeeDbContext);

        foreach (var d in services.Where(IsEf).ToList())
        {
            services.Remove(d);
        }

        services.AddDbContext<EmployeeDbContext>(options =>
            options.UseInMemoryDatabase(dbName));
    }
}

/// <summary>
/// Shared <see cref="WebApplicationFactory{Program}"/> for integration tests.
/// Lives in its own file so it is discoverable at compile time.
/// This factory owns the test-only InMemory swap: Program.cs always registers
/// SQL Server, and this factory removes that registration and re-registers an
/// isolated InMemory <see cref="EmployeeDbContext"/> (keyed by <see cref="DbName"/>),
/// regardless of host environment — so Development/Production pipeline tests
/// never touch the real SQL Server connection string or wipe a real database.
/// The InMemory package is referenced by the test project only and no longer
/// ships in the production binary.
/// </summary>
public sealed class TestApiFactory : WebApplicationFactory<Program>
{
    private readonly string _environment;
    private readonly string? _httpsPort;

    public TestApiFactory(string environment = "Testing", string? httpsPort = null)
    {
        _environment = environment;
        _httpsPort = httpsPort;
    }

    public string DbName { get; } = Guid.NewGuid().ToString();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.UseSetting("TESTING_DB_NAME", DbName);
        if (_httpsPort is not null)
        {
            // Program.cs decides on UseHttpsRedirection via
            // builder.Configuration["ASPNETCORE_HTTPS_PORT"]. UseSetting is the
            // mechanism proven to flow into Program's WebApplicationBuilder
            // configuration (same as TESTING_DB_NAME); the in-memory source is
            // a belt-and-suspenders fallback.
            builder.UseSetting("ASPNETCORE_HTTPS_PORT", _httpsPort);
            builder.ConfigureAppConfiguration(config =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ASPNETCORE_HTTPS_PORT"] = _httpsPort,
                }));
            // TestServer has no real HTTPS listener, so configure a known HTTPS
            // port for the redirect middleware (it reads IOptions, not a raw
            // singleton).
            builder.ConfigureServices(services => services.Configure<HttpsRedirectionOptions>(
                o => o.HttpsPort = int.Parse(_httpsPort)));
        }

        // InMemory swap owned by the factory (Program.cs always registers
        // SQL Server). See TestDbSwap.
        builder.ConfigureServices(services =>
            TestDbSwap.ReplaceWithInMemory(services, DbName));
    }
}
