using Employee_Api.Controllers;
using Employee_Api.DTOs;
using Employee_Api.Services;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace Employee_Api.Tests;

public class EmployeesControllerPagingTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task GetAll_BadRequest_WhenPageNumberInvalid(int page)
    {
        var svc = new Mock<IEmployeeService>();
        var ctl = new EmployeesController(svc.Object);
        var res = await ctl.GetAll(page, 10, null);
        Assert.IsType<BadRequestObjectResult>(res.Result);
        svc.Verify(s => s.GetPagedAsync(It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task GetAll_BadRequest_WhenPageSizeInvalid(int size)
    {
        var svc = new Mock<IEmployeeService>();
        var ctl = new EmployeesController(svc.Object);
        var res = await ctl.GetAll(1, size, null);
        Assert.IsType<BadRequestObjectResult>(res.Result);
    }

    [Fact]
    public async Task GetAll_Ok_WithEnvelope()
    {
        var svc = new Mock<IEmployeeService>();
        svc.Setup(s => s.GetPagedAsync("HR", 1, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<EmployeeDto>
            {
                Items = new List<EmployeeDto> { new() { EmployeeId = 1 } },
                TotalCount = 1, PageNumber = 1, PageSize = 10, TotalPages = 1
            });
        var ctl = new EmployeesController(svc.Object);
        var res = await ctl.GetAll(1, 10, "HR");
        var ok = Assert.IsType<OkObjectResult>(res.Result);
        var env = Assert.IsType<PagedResult<EmployeeDto>>(ok.Value);
        Assert.Equal(1, env.TotalCount);
    }
}
