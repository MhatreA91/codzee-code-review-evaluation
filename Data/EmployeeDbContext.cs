using Employee_Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Employee_Api.Data;

/// <summary>
/// EF Core DbContext configured via DI in Program.cs.
/// </summary>
public class EmployeeDbContext : DbContext
{
    public EmployeeDbContext(DbContextOptions<EmployeeDbContext> options)
        : base(options)
    {
    }

    public DbSet<Employee> Employees => Set<Employee>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Employee>(entity =>
        {
            entity.HasKey(e => e.EmployeeId);

            entity.Property(e => e.FirstName).HasMaxLength(50).IsRequired();
            entity.Property(e => e.LastName).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Email).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Department).HasMaxLength(50).IsRequired();
            entity.Property(e => e.Salary).HasColumnType("decimal(18,2)");

            // Enforce unique email at DB level for duplicate handling.
            entity.HasIndex(e => e.Email).IsUnique();
        });
    }
}
