using Employee_Api.DTOs;
using Employee_Api.Models;

namespace Employee_Api.Mappings;

/// <summary>
/// Simple manual mapper — no AutoMapper dependency.
/// </summary>
public static class EmployeeMapper
{
    public static EmployeeDto ToDto(Employee e) => new()
    {
        EmployeeId = e.EmployeeId,
        FirstName = e.FirstName,
        LastName = e.LastName,
        Email = e.Email,
        Department = e.Department,
        Salary = e.Salary,
        DateOfJoining = e.DateOfJoining
    };

    public static Employee ToEntity(CreateEmployeeDto dto) => new()
    {
        FirstName = dto.FirstName.Trim(),
        LastName = dto.LastName.Trim(),
        Email = dto.Email.Trim(),
        Department = dto.Department.Trim(),
        Salary = dto.Salary,
        DateOfJoining = dto.DateOfJoining
    };

    public static void ApplyUpdate(Employee entity, UpdateEmployeeDto dto)
    {
        entity.FirstName = dto.FirstName.Trim();
        entity.LastName = dto.LastName.Trim();
        entity.Email = dto.Email.Trim();
        entity.Department = dto.Department.Trim();
        entity.Salary = dto.Salary;
        entity.DateOfJoining = dto.DateOfJoining;
    }
}
