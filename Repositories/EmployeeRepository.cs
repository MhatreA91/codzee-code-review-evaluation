using Employee_Api.Data;
using Employee_Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Employee_Api.Repositories;

/// <summary>
/// EF Core implementation. AsNoTracking for read-only queries.
/// </summary>
public class EmployeeRepository : IEmployeeRepository
{
    private readonly EmployeeDbContext _context;

    public EmployeeRepository(EmployeeDbContext context)
    {
        _context = context;
    }

    public async Task<List<Employee>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _context.Employees
            .AsNoTracking()
            .OrderBy(e => e.EmployeeId)
            .ToListAsync(cancellationToken);
    }

    public async Task<Employee?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        return await _context.Employees
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.EmployeeId == id, cancellationToken);
    }

    public async Task<Employee?> GetByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        return await _context.Employees
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Email == email, cancellationToken);
    }

    public async Task<Employee> AddAsync(Employee employee, CancellationToken cancellationToken = default)
    {
        _context.Employees.Add(employee);
        await _context.SaveChangesAsync(cancellationToken);
        return employee;
    }

    public async Task UpdateAsync(Employee employee, CancellationToken cancellationToken = default)
    {
        // Attach + mark modified since GetById uses AsNoTracking.
        _context.Employees.Update(employee);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Employee employee, CancellationToken cancellationToken = default)
    {
        // Ensure tracked before delete (employee came from AsNoTracking query).
        var tracked = await _context.Employees.FindAsync(new object[] { employee.EmployeeId }, cancellationToken);
        if (tracked is not null)
        {
            _context.Employees.Remove(tracked);
        }
        else
        {
            _context.Employees.Attach(employee);
            _context.Employees.Remove(employee);
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    public Task<bool> ExistsAsync(int id, CancellationToken cancellationToken = default)
    {
        return _context.Employees.AnyAsync(e => e.EmployeeId == id, cancellationToken);
    }

    public Task<bool> EmailExistsAsync(string email, int? excludeEmployeeId = null, CancellationToken cancellationToken = default)
    {
        IQueryable<Employee> query = _context.Employees.AsNoTracking().Where(e => e.Email == email);
        if (excludeEmployeeId.HasValue)
        {
            query = query.Where(e => e.EmployeeId != excludeEmployeeId.Value);
        }
        // IQueryable used internally only — not exposed to callers.
        return query.AnyAsync(cancellationToken);
    }
}
