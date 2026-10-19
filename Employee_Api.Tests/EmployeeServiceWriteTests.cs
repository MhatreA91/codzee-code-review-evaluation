using Employee_Api.DTOs;
using Employee_Api.Models;
using Employee_Api.Repositories;
using Employee_Api.Services;
using Moq;

namespace Employee_Api.Tests;

public class EmployeeServiceWriteTests
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

    private static CreateEmployeeDto NewDto(string email) => new()
    {
        FirstName = "  Aarav ",
        LastName = " Sharma ",
        Email = " " + email + " ",
        Department = " Engineering ",
        Salary = 75000,
        DateOfJoining = new DateTime(2022, 3, 15)
    };

    [Fact]
    public async Task GetById_ReturnsNull_WhenMissing()
    {
        var repo = new Mock<IEmployeeRepository>();
        repo.Setup(r => r.GetByIdAsync(99, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Employee?)null);
        var sut = new EmployeeService(repo.Object);
        Assert.Null(await sut.GetByIdAsync(99));
    }

    [Fact]
    public async Task Create_TrimsAndMaps_WhenUnique()
    {
        var repo = new Mock<IEmployeeRepository>();
        repo.Setup(r => r.EmailExistsAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        repo.Setup(r => r.AddAsync(It.IsAny<Employee>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Employee e, CancellationToken _) => { e.EmployeeId = 7; return e; });
        var sut = new EmployeeService(repo.Object);
        var result = await sut.CreateAsync(NewDto("a.b@example.com"));
        Assert.Equal(7, result.EmployeeId);
        Assert.Equal("Aarav", result.FirstName);
        Assert.Equal("a.b@example.com", result.Email);
    }
}
