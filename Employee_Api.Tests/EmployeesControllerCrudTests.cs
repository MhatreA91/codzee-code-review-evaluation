using Employee_Api.Controllers;
using Employee_Api.DTOs;
using Employee_Api.Services;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace Employee_Api.Tests;

public class EmployeesControllerCrudTests
{
    private static EmployeeDto Dto(int id) => new()
    {
        EmployeeId = id, FirstName = "F", LastName = "L",
        Email = "user" + id + "@example.com", Department = "HR",
        Salary = 1, DateOfJoining = new DateTime(2022, 1, 1)
    };

    private static CreateEmployeeDto NewDto() => new()
    {
        FirstName = "A", LastName = "B", Email = "a@example.com",
        Department = "HR", Salary = 1, DateOfJoining = new DateTime(2022, 1, 1)
    };

    [Fact]
    public async Task GetById_Ok_WhenFound()
    {
        var svc = new Mock<IEmployeeService>();
        svc.Setup(s => s.GetByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(Dto(1));
        var ctl = new EmployeesController(svc.Object);
        var res = await ctl.GetById(1, default);
        var ok = Assert.IsType<OkObjectResult>(res.Result);
        Assert.Equal(Dto(1).Email, Assert.IsType<EmployeeDto>(ok.Value).Email);
    }

    [Fact]
    public async Task GetById_NotFound_WhenMissing()
    {
        var svc = new Mock<IEmployeeService>();
        svc.Setup(s => s.GetByIdAsync(9, It.IsAny<CancellationToken>())).ReturnsAsync((EmployeeDto?)null);
        var res = await new EmployeesController(svc.Object).GetById(9, default);
        Assert.IsType<NotFoundObjectResult>(res.Result);
    }

    [Fact]
    public async Task Create_ReturnsCreatedAtAction()
    {
        var svc = new Mock<IEmployeeService>();
        svc.Setup(s => s.CreateAsync(It.IsAny<CreateEmployeeDto>(), It.IsAny<CancellationToken>())).ReturnsAsync(Dto(5));
        var res = await new EmployeesController(svc.Object).Create(NewDto(), default);
        var created = Assert.IsType<CreatedAtActionResult>(res.Result);
        Assert.Equal(5, Assert.IsType<EmployeeDto>(created.Value).EmployeeId);
    }
}
