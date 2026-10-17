using Employee_Api.Models;
using Employee_Api.Repositories;
using Employee_Api.Services;
using Moq;

namespace Employee_Api.Tests;

public class EmployeeServicePagingTests
{
    private static Employee E(int id, string d) => new()
    {
        EmployeeId = id, FirstName = "F", LastName = "L",
        Email = $"u{id}@example.com", Department = d,
        Salary = 1000, DateOfJoining = new DateTime(2022, 1, 1)
    };

    [Fact]
    public async Task Paged_MapsAndComputesPages()
    {
        var repo = new Mock<IEmployeeRepository>();
        repo.Setup(r => r.GetPagedAsync(null, 2, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new List<Employee> { E(11, "HR"), E(12, "HR") }, 25));
        var sut = new EmployeeService(repo.Object);
        var res = await sut.GetPagedAsync(null, 2, 10);
        Assert.Equal(25, res.TotalCount);
        Assert.Equal(3, res.TotalPages);
        Assert.Equal(2, res.Items.Count);
    }

    [Fact]
    public async Task Paged_TrimsDept()
    {
        var repo = new Mock<IEmployeeRepository>();
        repo.Setup(r => r.GetPagedAsync("HR", 1, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new List<Employee> { E(2, "HR") }, 1));
        var sut = new EmployeeService(repo.Object);
        var res = await sut.GetPagedAsync("  HR  ", 1, 10);
        Assert.Single(res.Items);
        repo.Verify(r => r.GetPagedAsync("HR", 1, 10, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Paged_NullDept_WhenBlank(string? dept)
    {
        var repo = new Mock<IEmployeeRepository>();
        repo.Setup(r => r.GetPagedAsync(null, 1, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new List<Employee>(), 0));
        var sut = new EmployeeService(repo.Object);
        var res = await sut.GetPagedAsync(dept, 1, 10);
        Assert.Equal(0, res.TotalPages);
        repo.Verify(r => r.GetPagedAsync(null, 1, 10, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Paged_Empty_OutOfRange()
    {
        var repo = new Mock<IEmployeeRepository>();
        repo.Setup(r => r.GetPagedAsync("Legal", 99, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new List<Employee>(), 0));
        var sut = new EmployeeService(repo.Object);
        var res = await sut.GetPagedAsync("Legal", 99, 10);
        Assert.Empty(res.Items);
        Assert.Equal(0, res.TotalPages);
    }

    [Theory]
    [InlineData(20, 10, 2)]
    [InlineData(21, 10, 3)]
    [InlineData(1, 10, 1)]
    public async Task Paged_CeilingMath(int total, int size, int pages)
    {
        var repo = new Mock<IEmployeeRepository>();
        repo.Setup(r => r.GetPagedAsync(null, 1, size, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new List<Employee>(), total));
        var sut = new EmployeeService(repo.Object);
        Assert.Equal(pages, (await sut.GetPagedAsync(null, 1, size)).TotalPages);
    }
}
