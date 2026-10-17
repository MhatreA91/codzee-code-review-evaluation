using Employee_Api.Data;
using Employee_Api.Repositories;
using Employee_Api.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Swagger generator (Swashbuckle) — single docs source.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// EF Core: SQL Server via connection string in appsettings.json (no hardcoded credentials).
builder.Services.AddDbContext<EmployeeDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

// Repository + Service layer DI
builder.Services.AddScoped<IEmployeeRepository, EmployeeRepository>();
builder.Services.AddScoped<IEmployeeService, EmployeeService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Convenience: opening http://localhost:5174/ lands on the Swagger UI instead of 404.
app.MapGet("/", () => Results.Redirect("/swagger"));

// Only redirect HTTP -> HTTPS when an HTTPS endpoint is configured.
// The plain "http" launch profile has no HTTPS port, so this avoids the
// "Failed to determine the https port for redirect" warning.
var httpsPort = builder.Configuration.GetValue<int?>("ASPNETCORE_HTTPS_PORT");
if (app.Urls.Any(u => u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) || httpsPort.HasValue)
{
    app.UseHttpsRedirection();
}

app.UseAuthorization();

app.MapControllers();

app.Run();

