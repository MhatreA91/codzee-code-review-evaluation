using Employee_Api.Controllers;
using Employee_Api.DTOs;
using Employee_Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using System.Reflection;
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
    /// The 400 contract for ValidationProblem results: pre-execution both
    /// <see cref="ObjectResult.StatusCode"/> and
    /// <see cref="ValidationProblemDetails.Status"/> are null (the status is
    /// materialized by the MVC pipeline at execution time), so the strongest
    /// unit-level 400 assertion is the declared contract — the
    /// <c>[DefaultStatusCode(400)]</c> attribute on the ValidationProblem
    /// overload the controller called — plus the error payload.
    /// </summary>
    private static void AssertValidationProblem400(MethodBase? caller, ObjectResult result, string errorKey)
    {
        var details = Assert.IsType<ValidationProblemDetails>(result.Value);
        Assert.True(details.Errors.ContainsKey(errorKey));
        Assert.NotNull(caller);
        var attr = caller!.GetCustomAttributes(typeof(DefaultStatusCodeAttribute), inherit: false)
            .Cast<DefaultStatusCodeAttribute>()
            .SingleOrDefault(a => a.StatusCode == StatusCodes.Status400BadRequest);
        Assert.NotNull(attr);
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
        var ctl = new EmployeesController(new Mock<IEmployeeService>().Object);
        ctl.ModelState.AddModelError("Email", "bad");
        var res = await ctl.Create(C(), default);
        // 400 contract: payload carries the error key AND the result honors
        // the framework's DefaultStatusCode(400) declaration — a 500
        // ObjectResult regression cannot pass silently.
        var result = Assert.IsType<ObjectResult>(res.Result);
        AssertValidationProblem400(typeof(ControllerBase).GetMethod(
            nameof(ControllerBase.ValidationProblem),
            [typeof(ModelStateDictionary)])!, result, "Email");
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
        var ctl = new EmployeesController(svc.Object);
        Assert.IsType<OkObjectResult>((await ctl.Update(1, U(), default)).Result);
        Assert.IsType<NotFoundObjectResult>((await ctl.Update(2, U(), default)).Result);
        Assert.IsType<ConflictObjectResult>((await ctl.Update(3, U(), default)).Result);
        ctl.ModelState.AddModelError("x", "bad");
        var invalid = Assert.IsType<ObjectResult>((await ctl.Update(1, U(), default)).Result);
        AssertValidationProblem400(typeof(ControllerBase).GetMethod(
            nameof(ControllerBase.ValidationProblem),
            [typeof(ModelStateDictionary)])!, invalid, "x");
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
