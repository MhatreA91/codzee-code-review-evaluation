using Employee_Api.DTOs;
using Employee_Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Employee_Api.Controllers;

/// <summary>
/// Employee CRUD endpoints. Uses DTOs only — entity never exposed.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class EmployeesController : ControllerBase
{
    private readonly IEmployeeService _service;

    public EmployeesController(IEmployeeService service)
    {
        _service = service;
    }

    // GET /api/employees — legacy contract: returns full bare array.
    // Kept for backward compatibility; existing clients parse an array
    // and expect every employee when no query string is supplied.
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<EmployeeDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<EmployeeDto>>> GetAll(CancellationToken cancellationToken)
    {
        var employees = await _service.GetAllAsync(cancellationToken);
        return Ok(employees);
    }

    // GET /api/employees/paged?pageNumber=1&pageSize=10&department=HR
    // New opt-in pagination endpoint. Returns a PagedResult envelope;
    // does NOT change the shape of GET /api/employees above.
    [HttpGet("paged")]
    [ProducesResponseType(typeof(PagedResult<EmployeeDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<EmployeeDto>>> GetPaged(
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 10,
        [FromQuery] string? department = null,
        CancellationToken cancellationToken = default)
    {
        if (pageNumber < 1)
        {
            return BadRequest(new { message = "pageNumber must be >= 1." });
        }

        if (pageSize < 1 || pageSize > 100)
        {
            return BadRequest(new { message = "pageSize must be between 1 and 100." });
        }

        var result = await _service.GetPagedAsync(department, pageNumber, pageSize, cancellationToken);
        return Ok(result);
    }

    // GET /api/employees/{id}
    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(EmployeeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EmployeeDto>> GetById(int id, CancellationToken cancellationToken)
    {
        var employee = await _service.GetByIdAsync(id, cancellationToken);
        if (employee is null)
        {
            return NotFound(new { message = $"Employee with ID {id} not found." });
        }
        return Ok(employee);
    }

    // POST /api/employees
    [HttpPost]
    [ProducesResponseType(typeof(EmployeeDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<EmployeeDto>> Create([FromBody] CreateEmployeeDto dto, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        try
        {
            var created = await _service.CreateAsync(dto, cancellationToken);
            return CreatedAtAction(nameof(GetById), new { id = created.EmployeeId }, created);
        }
        catch (InvalidOperationException ex)
        {
            // Duplicate email
            return Conflict(new { message = ex.Message });
        }
    }

    // PUT /api/employees/{id}
    [HttpPut("{id:int}")]
    [ProducesResponseType(typeof(EmployeeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<EmployeeDto>> Update(int id, [FromBody] UpdateEmployeeDto dto, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        try
        {
            var updated = await _service.UpdateAsync(id, dto, cancellationToken);
            if (updated is null)
            {
                return NotFound(new { message = $"Employee with ID {id} not found." });
            }
            return Ok(updated);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    // DELETE /api/employees/{id}
    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        var deleted = await _service.DeleteAsync(id, cancellationToken);
        if (!deleted)
        {
            return NotFound(new { message = $"Employee with ID {id} not found." });
        }
        return NoContent();
    }
}
