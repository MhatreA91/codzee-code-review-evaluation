using Employee_Api.DTOs;
using Employee_Api.Mappings;
using Employee_Api.Models;

namespace Employee_Api.Tests;

public class EmployeeMapperTests
{
    [Fact]
    public void ToDto_MapsAllFields()
    {
        var e = new Employee
        {
            EmployeeId = 3, FirstName = "A", LastName = "B",
            Email = "a@example.com", Department = "IT",
            Salary = 123, DateOfJoining = new DateTime(2020, 5, 5)
        };
        var d = EmployeeMapper.ToDto(e);
        Assert.Equal(3, d.EmployeeId);
        Assert.Equal("a@example.com", d.Email);
    }

    [Fact]
    public void ToEntity_TrimsStrings()
    {
        var c = new CreateEmployeeDto
        {
            FirstName = " A ", LastName = " B ", Email = " e@example.com ",
            Department = " HR ", Salary = 10, DateOfJoining = new DateTime(2021, 1, 1)
        };
        var e = EmployeeMapper.ToEntity(c);
        Assert.Equal("A", e.FirstName);
        Assert.Equal("e@example.com", e.Email);
    }
}
