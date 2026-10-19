using System.ComponentModel.DataAnnotations;

namespace Employee_Api.DTOs;

/// <summary>
/// Response DTO — never expose the entity directly.
/// </summary>
public class EmployeeDto
{
    public int EmployeeId { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public decimal Salary { get; set; }
    public DateTime DateOfJoining { get; set; }
}

/// <summary>
/// Generic paged response envelope for list endpoints.
/// Serialized as camelCase JSON: items, totalCount, pageNumber, pageSize, totalPages.
/// </summary>
public class PagedResult<T>
{
    public List<T> Items { get; set; } = new();
    public int TotalCount { get; set; }
    public int PageNumber { get; set; }
    public int PageSize { get; set; }
    public int TotalPages { get; set; }
}

/// <summary>
/// DTO for POST /api/employees
/// </summary>
public class CreateEmployeeDto
{
    [Required, MaxLength(50)]
    public string FirstName { get; set; } = string.Empty;

    [Required, MaxLength(50)]
    public string LastName { get; set; } = string.Empty;

    [Required, EmailAddress, MaxLength(100)]
    public string Email { get; set; } = string.Empty;

    [Required, MaxLength(50)]
    public string Department { get; set; } = string.Empty;

    [Range(0, double.MaxValue, ErrorMessage = "Salary must be non-negative.")]
    public decimal Salary { get; set; }

    [Required]
    public DateTime DateOfJoining { get; set; }
}

/// <summary>
/// DTO for PUT /api/employees/{id}
/// </summary>
public class UpdateEmployeeDto
{
    [Required, MaxLength(50)]
    public string FirstName { get; set; } = string.Empty;

    [Required, MaxLength(50)]
    public string LastName { get; set; } = string.Empty;

    [Required, EmailAddress, MaxLength(100)]
    public string Email { get; set; } = string.Empty;

    [Required, MaxLength(50)]
    public string Department { get; set; } = string.Empty;

    [Range(0, double.MaxValue, ErrorMessage = "Salary must be non-negative.")]
    public decimal Salary { get; set; }

    [Required]
    public DateTime DateOfJoining { get; set; }
}
