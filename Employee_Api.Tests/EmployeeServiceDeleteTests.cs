using Employee_Api.Models;
using Employee_Api.Repositories;
using Employee_Api.Services;
using Moq;

namespace Employee_Api.Tests;

public class EmployeeServiceDeleteTests
{
    [Fact]
    public async Task Delete_True_WhenFound()
    {
        var ent = new Employee { EmployeeId = 5, Email = "g@example.com", Department = "S" };
        var repo = new Mock<IEmployeeRepository>();
        repo.Setup(r => r.GetByIdAsync(5, It.IsAny<CancellationToken>())).ReturnsAsync(ent);
        var sut = new EmployeeService(repo.Object);
        Assert.True(await sut.DeleteAsync(5));
        repo.Verify(r => r.DeleteAsync(ent, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Delete_False_WhenMissing()
    {
        var repo = new Mock<IEmployeeRepository>();
        repo.Setup(r => r.GetByIdAsync(8, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Employee?)null);
        var sut = new EmployeeService(repo.Object);
        Assert.False(await sut.DeleteAsync(8));
    }
}
