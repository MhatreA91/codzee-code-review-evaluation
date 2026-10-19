using Employee_Api.Data;
using Employee_Api.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Employee_Api.Tests;

public class DbContextModelTests
{
    private const string SqlServerProvider = "Microsoft.EntityFrameworkCore.SqlServer";

    private static EmployeeDbContext Ctx()
    {
        var opts = new DbContextOptionsBuilder<EmployeeDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        return new EmployeeDbContext(opts);
    }

    [Fact]
    public void Model_HasKey_Index_MaxLengths()
    {
        using var ctx = Ctx();
        var et = ctx.Model.FindEntityType(typeof(Employee_Api.Models.Employee))!;
        Assert.Equal("EmployeeId", et.FindPrimaryKey()!.Properties.Single().Name);
        Assert.Contains(et.GetIndexes(), i => i.IsUnique && i.Properties.Any(p => p.Name == "Email"));
        Assert.Equal(50, et.FindProperty("FirstName")!.GetMaxLength());
        Assert.Equal(100, et.FindProperty("Email")!.GetMaxLength());
        Assert.False(et.FindProperty("FirstName")!.IsNullable);
    }

    [Fact]
    public void Model_SqlServer_MapsTable_And_Decimal()
    {
        var opts = new DbContextOptionsBuilder<EmployeeDbContext>()
            .UseSqlServer("Server=(local);Database=Fake;Trusted_Connection=True;TrustServerCertificate=True").Options;
        using var ctx = new EmployeeDbContext(opts);
        var et = ctx.Model.FindEntityType(typeof(Employee_Api.Models.Employee))!;
        Assert.Equal("Employees", et.GetTableName());
        Assert.Equal("decimal(18,2)", et.FindProperty("Salary")!.GetColumnType());
    }

    [Fact]
    public void Snapshot_TypeExists_WithDbContextAttribute()
    {
        var t = typeof(EmployeeDbContext).Assembly.GetType("Employee_Api.Migrations.EmployeeDbContextModelSnapshot")!;
        var attr = (DbContextAttribute)Attribute.GetCustomAttribute(t, typeof(DbContextAttribute))!;
        Assert.Equal(typeof(EmployeeDbContext), attr.ContextType);
        Assert.True(t.IsSubclassOf(typeof(ModelSnapshot)));
    }

    private static void RunMigration(object mig, string method, MigrationBuilder builder)
    {
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        mig.GetType().GetMethod(method, flags)!.Invoke(mig, new object[] { builder });
    }

    [Fact]
    public void Migration_Up_Builds_CreateTable_And_CreateIndex()
    {
        var builder = new MigrationBuilder(SqlServerProvider);
        RunMigration(new InitialCreate(), "Up", builder);
        Assert.Contains(builder.Operations, o => o is CreateTableOperation t && t.Name == "Employees");
        Assert.Contains(builder.Operations, o => o is CreateIndexOperation i && i.Table == "Employees" && i.IsUnique);
    }

    [Fact]
    public void Migration_Down_Builds_DropTable()
    {
        var builder = new MigrationBuilder(SqlServerProvider);
        RunMigration(new InitialCreate(), "Down", builder);
        Assert.Contains(builder.Operations, o => o is DropTableOperation d && d.Name == "Employees");
    }

    [Fact]
    public void Snapshot_BuildModel_Executes()
    {
        var type = typeof(EmployeeDbContext).Assembly.GetType("Employee_Api.Migrations.EmployeeDbContextModelSnapshot")!;
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var instance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
        var builder = new ModelBuilder();
        type.GetMethod("BuildModel", flags)!.Invoke(instance, new object[] { builder });

        // Assert on the built model so an empty/wrong snapshot cannot pass silently.
        // NOTE: invoking BuildModel on an uninitialized snapshot instance
        // yields a shadow entity (no CLR type), so match by entity name.
        // Relational annotations (column types) need an initialized model, so
        // assert structural + annotation state available pre-initialization.
        var entity = Assert.Single(builder.Model.GetEntityTypes(), e => e.Name == "Employee_Api.Models.Employee");
        Assert.Equal("EmployeeId", entity.FindPrimaryKey()!.Properties.Single().Name);
        Assert.Equal("Employees", entity.GetTableName());
        Assert.Contains(entity.GetIndexes(), i => i.IsUnique && i.Properties.Any(p => p.Name == "Email"));
        var salary = entity.FindProperty("Salary")!;
        Assert.Equal("decimal(18,2)", salary.FindAnnotation("Relational:ColumnType")?.Value);
        Assert.True(entity.GetProperties().Count() >= 7);
    }

    [Fact]
    public void MigrationDesigner_BuildTargetModel_Executes()
    {
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        new InitialCreate().GetType().GetMethod("BuildTargetModel", flags)!.Invoke(new InitialCreate(), new object[] { new ModelBuilder() });
    }}

