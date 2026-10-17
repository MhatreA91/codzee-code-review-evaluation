using Employee_Api.Data;
using Employee_Api.Models;
using Employee_Api.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Employee_Api.Tests;

public class EmployeeRepositoryTests : IDisposable
{
    private readonly EmployeeDbContext _ctx;
    private readonly EmployeeRepository _sut;

    public EmployeeRepositoryTests()
    {
        var opts = new DbContextOptionsBuilder<EmployeeDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _ctx = new EmployeeDbContext(opts);
        _ctx.Employees.AddRange(
            new Employee { EmployeeId = 2, FirstName = "B", LastName = "B", Email = "b@e.com", Department = "HR", Salary = 2, DateOfJoining = new DateTime(2022, 1, 2) },
            new Employee { EmployeeId = 1, FirstName = "A", LastName = "A", Email = "a@e.com", Department = "IT", Salary = 1, DateOfJoining = new DateTime(2022, 1, 1) },
            new Employee { EmployeeId = 3, FirstName = "C", LastName = "C", Email = "c@e.com", Department = "HR", Salary = 3, DateOfJoining = new DateTime(2022, 1, 3) });
        _ctx.SaveChanges();
        _ctx.ChangeTracker.Clear();
        _sut = new EmployeeRepository(_ctx);
    }

    [Fact]
    public async Task GetAll_OrderedById()
    {
        var res = await _sut.GetAllAsync();
        Assert.Equal(new[] { 1, 2, 3 }, res.Select(e => e.EmployeeId));
    }

    [Fact]
    public async Task Paged_FiltersThenPages()
    {
        var (items, total) = await _sut.GetPagedAsync("HR", 1, 1);
        Assert.Equal(2, total);
        Assert.Single(items);
        Assert.Equal(2, items[0].EmployeeId);
        var (p2, _) = await _sut.GetPagedAsync("HR", 2, 1);
        Assert.Equal(3, p2[0].EmployeeId);
    }

    public void Dispose() => _ctx.Dispose();
}
