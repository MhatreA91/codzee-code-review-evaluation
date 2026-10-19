using Employee_Api.DTOs;
using Employee_Api.Mappings;
using Employee_Api.Models;
using Employee_Api.Repositories;

namespace Employee_Api.Services;

/// <summary>
/// Business logic: validation, duplicate-email checks, entity-DTO mapping.
/// </summary>
public class EmployeeService : IEmployeeService
{
    private readonly IEmployeeRepository _repository;

    public EmployeeService(IEmployeeRepository repository)
    {
        _repository = repository;
    }

    public async Task<List<EmployeeDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var employees = await _repository.GetAllAsync(cancellationToken);
        return employees.Select(EmployeeMapper.ToDto).ToList();
    }

    public async Task<PagedResult<EmployeeDto>> GetPagedAsync(string? department, int pageNumber, int pageSize, CancellationToken cancellationToken = default)
    {
        var normalizedDepartment = string.IsNullOrWhiteSpace(department) ? null : department.Trim();
        var (entities, totalCount) = await _repository.GetPagedAsync(normalizedDepartment, pageNumber, pageSize, cancellationToken);
        var dtos = entities.Select(EmployeeMapper.ToDto).ToList();
        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);

        return new PagedResult<EmployeeDto>
        {
            Items = dtos,
            TotalCount = totalCount,
            PageNumber = pageNumber,
            PageSize = pageSize,
            TotalPages = totalPages
        };
    }

    public async Task<EmployeeDto?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        var employee = await _repository.GetByIdAsync(id, cancellationToken);
        return employee is null ? null : EmployeeMapper.ToDto(employee);
    }

    public async Task<EmployeeDto> CreateAsync(CreateEmployeeDto dto, CancellationToken cancellationToken = default)
    {
        var email = dto.Email.Trim();

        if (await _repository.EmailExistsAsync(email, cancellationToken: cancellationToken))
        {
            throw new InvalidOperationException($"An employee with email '{email}' already exists.");
        }

        var entity = EmployeeMapper.ToEntity(dto);
        var created = await _repository.AddAsync(entity, cancellationToken);
        return EmployeeMapper.ToDto(created);
    }

    public async Task<EmployeeDto?> UpdateAsync(int id, UpdateEmployeeDto dto, CancellationToken cancellationToken = default)
    {
        // Fetch tracked entity for update. Repository GetById is AsNoTracking,
        // so re-fetch via repository then apply + update.
        var existing = await _repository.GetByIdAsync(id, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        var email = dto.Email.Trim();
        if (await _repository.EmailExistsAsync(email, excludeEmployeeId: id, cancellationToken: cancellationToken))
        {
            throw new InvalidOperationException($"An employee with email '{email}' already exists.");
        }

        var entity = new Employee
        {
            EmployeeId = existing.EmployeeId,
            FirstName = existing.FirstName,
            LastName = existing.LastName,
            Email = existing.Email,
            Department = existing.Department,
            Salary = existing.Salary,
            DateOfJoining = existing.DateOfJoining
        };
        EmployeeMapper.ApplyUpdate(entity, dto);

        await _repository.UpdateAsync(entity, cancellationToken);
        return EmployeeMapper.ToDto(entity);
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var existing = await _repository.GetByIdAsync(id, cancellationToken);
        if (existing is null)
        {
            return false;
        }

        await _repository.DeleteAsync(existing, cancellationToken);
        return true;
    }
}
