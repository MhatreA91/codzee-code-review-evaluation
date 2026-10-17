using Employee_Api.DTOs;

namespace Employee_Api.Services;

/// <summary>
/// Business-logic contract. Returns null when not found;
/// throws <see cref="InvalidOperationException"/> on duplicate email.
/// </summary>
public interface IEmployeeService
{
    Task<List<EmployeeDto>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<EmployeeDto?> GetByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<EmployeeDto> CreateAsync(CreateEmployeeDto dto, CancellationToken cancellationToken = default);
    Task<EmployeeDto?> UpdateAsync(int id, UpdateEmployeeDto dto, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default);
}
