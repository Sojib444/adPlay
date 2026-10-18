using AdPlay.Api.Domain;

namespace AdPlay.Api.Features.Analytics;

public sealed record DepartmentSalaryStats(
    int DepartmentId,
    int EmployeeCount,
    decimal? HighestSalary,
    decimal? LowestSalary,
    decimal? AverageSalary,
    decimal? MedianSalary);

public static class SalaryStatistics
{
    /// <summary>
    /// Sorts each department's salaries once (O(n log n) overall); min, max and median are then O(1) lookups.
    /// Departments in <paramref name="allDepartmentIds"/> with no employees get null statistics.
    /// </summary>
    public static List<DepartmentSalaryStats> Calculate(
        IEnumerable<Employee> employees,
        IEnumerable<int>? allDepartmentIds = null)
    {
        var byDepartment = employees
            .GroupBy(e => e.DepartmentId)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Salary).Order().ToArray());

        var departmentIds = allDepartmentIds is null
            ? byDepartment.Keys
            : allDepartmentIds.Union(byDepartment.Keys);

        return departmentIds
            .Order()
            .Select(id => FromSorted(id, byDepartment.GetValueOrDefault(id, [])))
            .ToList();
    }

    public static DepartmentSalaryStats FromSorted(int departmentId, decimal[] sorted)
    {
        if (sorted.Length == 0)
            return new DepartmentSalaryStats(departmentId, 0, null, null, null, null);

        return new DepartmentSalaryStats(
            departmentId,
            sorted.Length,
            HighestSalary: sorted[^1],
            LowestSalary: sorted[0],
            AverageSalary: Math.Round(sorted.Average(), 2, MidpointRounding.AwayFromZero),
            MedianSalary: Median(sorted));
    }

    /// <summary>Odd count: the middle value. Even count: mean of the two middle values.</summary>
    public static decimal Median(decimal[] sorted)
    {
        if (sorted.Length == 0) throw new ArgumentException("Median of an empty set is undefined.", nameof(sorted));
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2m;
    }
}
