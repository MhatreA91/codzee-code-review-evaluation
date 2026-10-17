using Employee_Api.Data;
using Employee_Api.Models;
using Employee_Api.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Employee_Api.Tests;

public class EmployeeRepositoryWriteTests : IDisposable
{
    private readonly EmployeeDbContext _ctx;
    private readonly EmployeeRepository _sut;

    public EmployeeRepositoryWriteTests()
    {
        var opts = new DbContextOptionsBuilder<EmployeeDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _ctx = new EmployeeDbContext(opts);
        _sut = new EmployeeRepository(_ctx);
    }

    [Fact]
    public async Task Add_GetById_GetByEmail_Exists()
    {
        var added = await _sut.AddAsync(new Employee
        {
            FirstName = "A", LastName = "B", Email = "w@e.com",
            Department = "HR", Salary = 10, DateOfJoining = new DateTime(2022, 1, 1)
        });
        Assert.True(added.EmployeeId > 0);
        Assert.NotNull(await _sut.GetByIdAsync(added.EmployeeId));
        Assert.NotNull(await _sut.GetByEmailAsync("w@e.com"));
        Assert.Null(await _sut.GetByEmailAsync("no@e.com"));
        Assert.True(await _sut.ExistsAsync(added.EmployeeId));
        Assert.False(await _sut.ExistsAsync(-1));
    }

    [Fact]
    public async Task EmailExists_WithExclude()
    {
        var a = await _sut.AddAsync(new Employee
        {
            FirstName = "A", LastName = "A", Email = "same@e.com",
            Department = "HR", Salary = 1, DateOfJoining = new DateTime(2022, 1, 1)
        });
        Assert.True(await _sut.EmailExistsAsync("same@e.com"));
        Assert.False(await _sut.EmailExistsAsync("same@e.com", a.EmployeeId));
        Assert.False(await _sut.EmailExistsAsync("other@e.com"));
    }

    [Fact]
    public async Task Update_Then_Delete()
    {
        var e = await _sut.AddAsync(new Employee
        {
            FirstName = "A", LastName = "B", Email = "u@e.com",
            Department = "HR", Salary = 1, DateOfJoining = new DateTime(2022, 1, 1)
        });
        _ctx.ChangeTracker.Clear();
        e.Department = "IT";
        await _sut.UpdateAsync(e);
        _ctx.ChangeTracker.Clear();
        Assert.Equal("IT", (await _sut.GetByIdAsync(e.EmployeeId))!.Department);
        await _sut.DeleteAsync(e);
        Assert.Null(await _sut.GetByIdAsync(e.EmployeeId));
    }

    public void Dispose() => _ctx.Dispose();
}
