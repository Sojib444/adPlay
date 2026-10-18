using AdPlay.Api.Common.Querying;
using AdPlay.Api.Domain;

namespace AdPlay.Tests;

public class QueryableSortExtensionsTests
{
    private static readonly Department Eng = new() { Id = 1, Name = "Engineering" };
    private static readonly Department Ops = new() { Id = 2, Name = "Ops" };

    private static IQueryable<Employee> Employees() => new List<Employee>
    {
        new() { Id = 1, Name = "Carol", Salary = 300, JoiningDate = new DateTime(2021, 1, 1), DepartmentId = 2, Department = Ops },
        new() { Id = 2, Name = "alice", Salary = 100, JoiningDate = new DateTime(2023, 1, 1), DepartmentId = 1, Department = Eng },
        new() { Id = 3, Name = "Bob", Salary = 300, JoiningDate = new DateTime(2022, 1, 1), DepartmentId = 1, Department = Eng },
    }.AsQueryable();

    [Fact]
    public void Sorts_ascending_by_default()
    {
        var ids = Employees().OrderBy("JoiningDate").Select(e => e.Id).ToList();
        Assert.Equal([1, 3, 2], ids);
    }

    [Fact]
    public void Sorts_descending_and_is_case_insensitive_for_name_and_direction()
    {
        var ids = Employees().OrderBy("salary DESC").ThenBy(e => e.Id).Select(e => e.Id).ToList();
        Assert.Equal([1, 3, 2], ids);
    }

    [Fact]
    public void Supports_multiple_clauses_and_nested_paths()
    {
        var ids = Employees().OrderBy("Department.Name, Salary desc").Select(e => e.Id).ToList();
        Assert.Equal([3, 2, 1], ids);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("DoesNotExist")]
    [InlineData("Name sideways")]
    [InlineData("Name asc extra")]
    [InlineData("Department")]            // navigation, not a scalar
    [InlineData("Name; DROP TABLE Employee")]
    [InlineData("A, B, C, D")]             // too many clauses
    public void Rejects_invalid_input(string spec)
    {
        Assert.Throws<InvalidSortException>(() => Employees().OrderBy(spec).ToList());
    }

    [Fact]
    public void Allowlist_blocks_properties_that_exist_but_are_not_permitted()
    {
        var ex = Assert.Throws<InvalidSortException>(() =>
            Employees().OrderBy("Salary", allowedProperties: ["Name", "JoiningDate"]).ToList());
        Assert.Contains("not allowed", ex.Message);
    }
}
