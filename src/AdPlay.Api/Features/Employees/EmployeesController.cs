using AdPlay.Api.Common;
using AdPlay.Api.Common.Querying;
using AdPlay.Api.Data;
using AdPlay.Api.Domain;
using AdPlay.Api.Features.Analytics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AdPlay.Api.Features.Employees;

public sealed record EmployeeListItem(int Id, string Name, string DepartmentName, decimal Salary, DateTime JoiningDate);

public sealed class EmployeeSearchQuery
{
    public string? Name { get; init; }
    public int? DepartmentId { get; init; }
    public decimal? MinSalary { get; init; }
    public decimal? MaxSalary { get; init; }
    public DateTime? JoinedFrom { get; init; }
    public string? SortBy { get; init; }
    public string? SortDirection { get; init; }
    public int? Page { get; init; }
    public int? PageSize { get; init; }
}

/// <summary>The filters and sort keys clients can use for employees.</summary>
public sealed class EmployeeSearchSpec : QuerySpec<Employee>
{
    private static readonly Dictionary<string, string> Sorts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["name"] = nameof(Employee.Name),
        ["salary"] = nameof(Employee.Salary),
        ["joiningDate"] = nameof(Employee.JoiningDate),
        ["department"] = "Department.Name"
    };

    protected override IReadOnlyDictionary<string, string> SortMap => Sorts;
    protected override string DefaultSort => "name";

    public EmployeeSearchSpec(EmployeeSearchQuery q)
    {
        SortBy = q.SortBy;
        Descending = string.Equals(q.SortDirection, "desc", StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(q.Name))
        {
            var prefix = q.Name.Trim();
            Where(e => e.Name.StartsWith(prefix)); // LIKE 'prefix%' — can use the Name index
        }
        if (q.DepartmentId is { } departmentId) Where(e => e.DepartmentId == departmentId);
        if (q.MinSalary is { } min) Where(e => e.Salary >= min);
        if (q.MaxSalary is { } max) Where(e => e.Salary <= max);
        if (q.JoinedFrom is { } from) Where(e => e.JoiningDate >= from);
    }
}

[ApiController]
[Route("api/employees")]
public sealed class EmployeesController(IReadRepository<Employee> employees, AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResult<EmployeeListItem>>> Search(
        [FromQuery] EmployeeSearchQuery query, CancellationToken ct)
    {
        if (query.SortDirection is not (null or "asc" or "desc"))
            return ValidationProblem(detail: "sortDirection must be 'asc' or 'desc'.");

        var result = await employees.PageAsync(
            new EmployeeSearchSpec(query),
            e => new EmployeeListItem(e.Id, e.Name, e.Department!.Name, e.Salary, e.JoiningDate),
            new PageRequest(query.Page, query.PageSize),
            ct);

        return Ok(result);
    }

    /// <summary>Loads only (DepartmentId, Salary) pairs; includes empty departments.</summary>
    [HttpGet("salary-stats")]
    public async Task<ActionResult<List<DepartmentSalaryStats>>> SalaryStats(CancellationToken ct)
    {
        var departmentIds = await db.Departments.AsNoTracking().Select(d => d.Id).ToListAsync(ct);
        var salaries = await db.Employees.AsNoTracking()
            .Select(e => new Employee { DepartmentId = e.DepartmentId, Salary = e.Salary })
            .ToListAsync(ct);

        return Ok(SalaryStatistics.Calculate(salaries, departmentIds));
    }
}
