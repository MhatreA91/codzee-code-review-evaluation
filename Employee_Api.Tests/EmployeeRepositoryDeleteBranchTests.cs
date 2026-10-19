using Employee_Api.Data;
using Employee_Api.Models;
using Employee_Api.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Employee_Api.Tests;

public class EmployeeRepositoryDeleteBranchTests : IDisposable
{
    private readonly EmployeeDbContext _ctx;
    private readonly EmployeeRepository _sut;

    public EmployeeRepositoryDeleteBranchTests()
    {
        var opts = new DbContextOptionsBuilder<EmployeeDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _ctx = new EmployeeDbContext(opts);
        _sut = new EmployeeRepository(_ctx);
    }

    [Fact]
    public async Task Delete_DetachedEntity_RemovesIt()
    {
        var e = await _sut.AddAsync(new Employee
        {
            FirstName = "A", LastName = "B", Email = "d@e.com",
            Department = "HR", Salary = 1, DateOfJoining = new DateTime(2022, 1, 1)
        });
        _ctx.ChangeTracker.Clear();
        var detached = new Employee { EmployeeId = e.EmployeeId, Email = "d@e.com" };
        await _sut.DeleteAsync(detached);
        Assert.Null(await _sut.GetByIdAsync(e.EmployeeId));
    }

    [Fact]
    public async Task Delete_NonExistentEntity_ExecutesAttachRemoveBranch()
    {
        // Arrange: entity never added, so FindAsync returns null and the
        // else branch (Attach + Remove) executes. The InMemory provider
        // throws DbUpdateConcurrencyException on SaveChanges for a phantom
        // row (SQL Server would silently no-op) — the exception proves the
        // else branch ran instead of the tracked-Remove branch.
        var ghost = new Employee { EmployeeId = 9999, Email = "ghost@e.com" };

        // Act + Assert
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => _sut.DeleteAsync(ghost));
    }

    [Fact]
    public async Task Paged_TrimsFilter_And_EmptyPages()
    {
        await _sut.AddAsync(new Employee
        {
            FirstName = "A", LastName = "B", Email = "p@e.com",
            Department = "HR", Salary = 1, DateOfJoining = new DateTime(2022, 1, 1)
        });
        _ctx.ChangeTracker.Clear();
        var (items, total) = await _sut.GetPagedAsync("  HR  ", 1, 10);
        Assert.Equal(1, total);
        Assert.Single(items);
        var (none, zero) = await _sut.GetPagedAsync("  ", 5, 10);
        Assert.Equal(1, zero);
        var (missing, mTotal) = await _sut.GetPagedAsync("Nope", 1, 10);
        Assert.Equal(0, mTotal);
        Assert.Empty(missing);
    }

    public void Dispose() => _ctx.Dispose();
}
