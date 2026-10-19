using Employee_Api.DTOs;
using Employee_Api.Models;
using Employee_Api.Repositories;
using Employee_Api.Services;
using Moq;

namespace Employee_Api.Tests;

public class EmployeeServiceCrudTests
{
    private static Employee Entity(int id, string dept, string email) => new()
    {
        EmployeeId = id,
        FirstName = "First" + id,
        LastName = "Last" + id,
        Email = email,
        Department = dept,
        Salary = 50000 + id,
        DateOfJoining = new DateTime(2022, 1, 1).AddDays(id)
    };

    [Fact]
    public async Task GetAll_ReturnsMappedDtos()
    {
        var repo = new Mock<IEmployeeRepository>();
        repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Employee>
            {
                Entity(1, "Engineering", "one@example.com"),
                Entity(2, "HR", "two@example.com")
            });
        var sut = new EmployeeService(repo.Object);
        var result = await sut.GetAllAsync();
        Assert.Equal(2, result.Count);
        Assert.Equal("one@example.com", result[0].Email);
    }

    [Fact]
    public async Task GetAll_ReturnsEmpty_WhenNone()
    {
        var repo = new Mock<IEmployeeRepository>();
        repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Employee>());
        var sut = new EmployeeService(repo.Object);
        Assert.Empty(await sut.GetAllAsync());
    }

    [Fact]
    public async Task GetById_ReturnsDto_WhenFound()
    {
        var repo = new Mock<IEmployeeRepository>();
        repo.Setup(r => r.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Entity(1, "Finance", "fin@example.com"));
        var sut = new EmployeeService(repo.Object);
        var result = await sut.GetByIdAsync(1);
        Assert.NotNull(result);
        Assert.Equal("fin@example.com", result.Email);
    }
}
