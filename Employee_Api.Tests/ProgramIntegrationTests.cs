using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Employee_Api.Data;
using Employee_Api.DTOs;
using Employee_Api.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
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
    }
}
