using Employee_Api.Controllers;
using Employee_Api.DTOs;
using Employee_Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Employee_Api.Tests;

public class EmployeesControllerWriteTests
{
    private static CreateEmployeeDto C() => new()
    {
        FirstName = "A", LastName = "B", Email = "a@e.com",
        Department = "HR", Salary = 1, DateOfJoining = new DateTime(2022, 1, 1)
    };
    private static UpdateEmployeeDto U() => new()
    {
        FirstName = "A", LastName = "B", Email = "a@e.com",
        Department = "HR", Salary = 1, DateOfJoining = new DateTime(2022, 1, 1)
    };
    private static EmployeeDto D(int id) => new()
    {
        EmployeeId = id, FirstName = "F", LastName = "L",
        Email = "u@e.com", Department = "HR", Salary = 1,
        DateOfJoining = new DateTime(2022, 1, 1)
    };

    /// <summary>
    /// Builds a controller wired to REAL MVC services (including
    /// <c>ProblemDetailsFactory</c>) via a service-backed
    /// <see cref="ControllerContext"/>, so <c>ValidationProblem(ModelState)</c>
    /// produces the same status-carrying result the real pipeline would. The
    /// tests then assert the REAL status through the reviewer's suggested
    /// path: <c>ExecuteResultAsync</c> with a controller context — a 500
    /// <see cref="ObjectResult"/> regression cannot pass because its wire
    /// status would not be 400.
    /// </summary>
    private static (EmployeesController Controller, IServiceProvider Services) CreateController(IEmployeeService service)
    {
        var services = new ServiceCollection().AddLogging().AddMvcCore().Services.BuildServiceProvider();
        var httpContext = new DefaultHttpContext { RequestServices = services };
        var ctl = new EmployeesController(service)
        {
            ControllerContext = new ControllerContext(new ActionContext(
                httpContext, new RouteData(), new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor()))
        };
        return (ctl, services);
    }

    /// <summary>
    /// Executes the CONTROLLER'S OWN result (not a framework constant) and
    /// asserts the real wire status is 400 plus the error payload key.
    /// </summary>
    private static async Task AssertWire400Async(ObjectResult result, string errorKey, IServiceProvider services)
    {
        var details = Assert.IsType<ValidationProblemDetails>(result.Value);
        Assert.True(details.Errors.ContainsKey(errorKey));

        var httpContext = new DefaultHttpContext { RequestServices = services };
        var actionContext = new ActionContext(
            httpContext, new RouteData(), new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor());
        await result.ExecuteResultAsync(actionContext);
        Assert.Equal(StatusCodes.Status400BadRequest, httpContext.Response.StatusCode);
    }

    [Fact]
    public async Task Create_Conflict_OnDuplicate()
    {
        var svc = new Mock<IEmployeeService>();
        svc.Setup(s => s.CreateAsync(It.IsAny<CreateEmployeeDto>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("dup"));
        var res = await new EmployeesController(svc.Object).Create(C(), default);
        Assert.IsType<ConflictObjectResult>(res.Result);
    }

    [Fact]
    public async Task Create_ValidationProblem_WhenInvalid()
    {
        var (ctl, services) = CreateController(new Mock<IEmployeeService>().Object);
        ctl.ModelState.AddModelError("Email", "bad");
        var res = await ctl.Create(C(), default);
        // Asserts the REAL status through ExecuteResultAsync on the
        // controller's own result — a 500 ObjectResult regression fails
        // because its wire status would not be 400.
        var result = Assert.IsAssignableFrom<ObjectResult>(res.Result);
        await AssertWire400Async(result, "Email", services);
    }

    [Fact]
    public async Task Update_Ok_NotFound_Conflict_Invalid()
    {
        var svc = new Mock<IEmployeeService>();
        svc.Setup(s => s.UpdateAsync(1, It.IsAny<UpdateEmployeeDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(D(1));
        svc.Setup(s => s.UpdateAsync(2, It.IsAny<UpdateEmployeeDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((EmployeeDto?)null);
        svc.Setup(s => s.UpdateAsync(3, It.IsAny<UpdateEmployeeDto>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("dup"));
        var (ctl, services) = CreateController(svc.Object);
        Assert.IsType<OkObjectResult>((await ctl.Update(1, U(), default)).Result);
        Assert.IsType<NotFoundObjectResult>((await ctl.Update(2, U(), default)).Result);
        Assert.IsType<ConflictObjectResult>((await ctl.Update(3, U(), default)).Result);
        ctl.ModelState.AddModelError("x", "bad");
        var invalid = Assert.IsAssignableFrom<ObjectResult>((await ctl.Update(1, U(), default)).Result);
        await AssertWire400Async(invalid, "x", services);
    }

    [Fact]
    public async Task Delete_NoContent_And_NotFound()
    {
        var svc = new Mock<IEmployeeService>();
        svc.Setup(s => s.DeleteAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        svc.Setup(s => s.DeleteAsync(2, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var ctl = new EmployeesController(svc.Object);
        Assert.IsType<NoContentResult>(await ctl.Delete(1, default));
        Assert.IsType<NotFoundObjectResult>(await ctl.Delete(2, default));
    }
}
