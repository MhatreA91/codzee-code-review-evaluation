using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Employee_Api.Data;
using Employee_Api.DTOs;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Employee_Api.Tests;

public class ProgramIntegrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static (TestApiFactory factory, System.Net.Http.HttpClient client) CreateSeededClient(string environment = "Testing")
    {
        var factory = new TestApiFactory(environment);
        var client = factory.CreateClient();
        {
            using var scope = factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<EmployeeDbContext>();
            context.Database.EnsureDeleted();
            context.Database.EnsureCreated();
            if (!context.Employees.Any())
            {
                context.Employees.AddRange(
                    new Employee_Api.Models.Employee { FirstName = "Asha", LastName = "K", Email = "asha.k@example.com", Department = "HR", Salary = 60000, DateOfJoining = new DateTime(2022, 1, 1) },
                    new Employee_Api.Models.Employee { FirstName = "Dev", LastName = "P", Email = "dev.p@example.com", Department = "Engineering", Salary = 60000, DateOfJoining = new DateTime(2022, 1, 1) },
                    new Employee_Api.Models.Employee { FirstName = "Mira", LastName = "S", Email = "mira.s@example.com", Department = "HR", Salary = 60000, DateOfJoining = new DateTime(2022, 1, 1) },
                    new Employee_Api.Models.Employee { FirstName = "Om", LastName = "T", Email = "om.t@example.com", Department = "Finance", Salary = 60000, DateOfJoining = new DateTime(2022, 1, 1) });
                context.SaveChanges();
            }

            return (factory, client);
        }
    }

    [Fact]
    public async Task GetEmployees_PagedEnvelope_EndToEnd()
    {
        var (factory, client) = CreateSeededClient();
        using (factory)
        using (client)
        using (var response = await client.GetAsync("/api/employees?pageNumber=1&pageSize=2"))
        {
            response.EnsureSuccessStatusCode();
            var page = await response.Content.ReadFromJsonAsync<PagedResult<EmployeeDto>>(JsonOptions);
            Assert.NotNull(page);
            Assert.Equal(4, page.TotalCount);
            Assert.Equal(2, page.TotalPages);
            Assert.Equal(2, page.Items.Count);
        }
    }

    [Fact]
    public async Task GetEmployees_DepartmentFilter_EndToEnd()
    {
        var (factory, client) = CreateSeededClient();
        using (factory)
        using (client)
        using (var response = await client.GetAsync("/api/employees?department=HR&pageNumber=1&pageSize=10"))
        {
            response.EnsureSuccessStatusCode();
            var page = await response.Content.ReadFromJsonAsync<PagedResult<EmployeeDto>>(JsonOptions);
            Assert.NotNull(page);
            Assert.Equal(2, page.TotalCount);
            Assert.All(page.Items, item => Assert.Equal("HR", item.Department));
        }
    }

    [Fact]
    public async Task GetEmployees_InvalidPage_Returns400_EndToEnd()
    {
        var (factory, client) = CreateSeededClient();
        using (factory)
        using (client)
        using (var response = await client.GetAsync("/api/employees?pageNumber=0&pageSize=10"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task Root_Redirects_To_Swagger()
    {
        var (factory, _) = CreateSeededClient();
        using (factory)
        using (var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }))
        using (var response = await client.GetAsync("/"))
        {
            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
            Assert.Equal("/swagger", response.Headers.Location?.ToString());
        }
    }

    [Fact]
    public async Task Pipeline_InDevelopment_ServesSwagger()
    {
        // Development environment covers the app.Environment.IsDevelopment()
        // branch in Program.cs. TestApiFactory forces an isolated InMemory
        // EmployeeDbContext, so this never touches the real SQL Server.
        var (factory, client) = CreateSeededClient(environment: "Development");
        using (factory)
        using (client)
        using (var response = await client.GetAsync("/swagger/v1/swagger.json"))
        {
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            Assert.Contains("openapi", json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Pipeline_Works_Outside_Development()
    {
        // Production environment covers the non-Development path.
        // TestApiFactory still swaps in InMemory, so no live SQL Server needed.
        using var factory = new TestApiFactory(environment: "Production");
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/employees?pageNumber=1&pageSize=10");
        response.EnsureSuccessStatusCode();
        // Swagger UI must NOT be served outside Development.
        using var swagger = await client.GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.NotFound, swagger.StatusCode);
    }

    [Fact]
    public async Task Pipeline_Builds_With_Https_Redirection_Configured()
    {
        // Pass the HTTPS port through the factory (per-host config) instead of
        // mutating the process-wide env var, and assert the middleware
        // actually redirects plain HTTP to HTTPS.
        using var factory = new TestApiFactory(httpsPort: "443");
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.GetAsync("/api/employees?pageNumber=1&pageSize=10");
        Assert.True(
            response.StatusCode is HttpStatusCode.TemporaryRedirect
                or HttpStatusCode.PermanentRedirect
                or HttpStatusCode.Redirect
                or HttpStatusCode.MovedPermanently
                or HttpStatusCode.Found,
            $"Expected an HTTP->HTTPS redirect, got {response.StatusCode}.");
        Assert.NotNull(response.Headers.Location);
        Assert.Equal("https", response.Headers.Location.Scheme);
    }
}
