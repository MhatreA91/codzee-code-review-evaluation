using Employee_Api.DTOs;
using Employee_Api.Models;
using Employee_Api.Repositories;
using Employee_Api.Services;
using Moq;

namespace Employee_Api.Tests;

public class EmployeeServiceCreateUpdateTests
{
    private static Employee Ent(int id, string d, string e) => new()
    {
        EmployeeId = id, FirstName = "F" + id, LastName = "L" + id,
        Email = e, Department = d, Salary = 50000,
        DateOfJoining = new DateTime(2022, 1, 1)
    };
    private static CreateEmployeeDto CDto(string e) => new()
    {
        FirstName = "A", LastName = "B", Email = e,
        Department = "HR", Salary = 1000, DateOfJoining = new DateTime(2022, 1, 1)
    };
    private static UpdateEmployeeDto UDto(string e) => new()
    {
        FirstName = "X", LastName = "Y", Email = e,
        Department = "IT", Salary = 2000, DateOfJoining = new DateTime(2023, 1, 1)
    };

    [Fact]
    public async Task Create_Throws_WhenDuplicateEmail()
    {
        var repo = new Mock<IEmployeeRepository>();
        repo.Setup(r => r.EmailExistsAsync("d@example.com", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var sut = new EmployeeService(repo.Object);
        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.CreateAsync(CDto("d@example.com")));
        repo.Verify(r => r.AddAsync(It.IsAny<Employee>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Update_ReturnsUpdated_WhenFound()
    {
        var repo = new Mock<IEmployeeRepository>();
        repo.Setup(r => r.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ent(1, "HR", "o@example.com"));
        repo.Setup(r => r.EmailExistsAsync("n@example.com", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var sut = new EmployeeService(repo.Object);
        var res = await sut.UpdateAsync(1, UDto("n@example.com"));
        Assert.NotNull(res);
        Assert.Equal("n@example.com", res.Email);
        Assert.Equal("IT", res.Department);
    }

    [Fact]
    public async Task Update_ReturnsNull_WhenMissing()
    {
        var repo = new Mock<IEmployeeRepository>();
        repo.Setup(r => r.GetByIdAsync(9, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Employee?)null);
        var sut = new EmployeeService(repo.Object);
        Assert.Null(await sut.UpdateAsync(9, UDto("a@example.com")));
    }

    [Fact]
    public async Task Update_Throws_WhenEmailTakenByOther()
    {
        var repo = new Mock<IEmployeeRepository>();
        repo.Setup(r => r.GetByIdAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Ent(1, "HR", "m@example.com"));
        repo.Setup(r => r.EmailExistsAsync("t@example.com", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var sut = new EmployeeService(repo.Object);
        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.UpdateAsync(1, UDto("t@example.com")));
    }
}
