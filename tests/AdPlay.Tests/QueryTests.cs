using AdPlay.Api.Common;
using AdPlay.Api.Common.Querying;
using AdPlay.Api.Domain;
using AdPlay.Api.Features.Employees;
using AdPlay.Api.Features.Products;
using AdPlay.Tests.Infrastructure;

namespace AdPlay.Tests;

public class ReadRepositoryTests
{
    private static async Task<SqliteTestDb> SeededAsync()
    {
        var db = await SqliteTestDb.CreateAsync();
        await using var ctx = db.CreateContext();
        var eng = new Department { Name = "Engineering" };
        var ops = new Department { Name = "Ops" };
        ctx.Employees.AddRange(
            new Employee { Name = "Carol", Department = ops, Salary = 1, JoiningDate = new DateTime(2021, 1, 1) },
            new Employee { Name = "Alice", Department = eng, Salary = 1, JoiningDate = new DateTime(2023, 1, 1) },
            new Employee { Name = "Bob", Department = eng, Salary = 1, JoiningDate = new DateTime(2022, 1, 1) },
            new Employee { Name = "Dave", Department = eng, Salary = 1, JoiningDate = new DateTime(2020, 1, 1) });
        await ctx.SaveChangesAsync();
        return db;
    }

    [Fact]
    public async Task Filters_sorts_pages_and_projects()
    {
        await using var db = await SeededAsync();
        await using var ctx = db.CreateContext();
        var repo = new EfReadRepository<Employee>(ctx);

        var engineeringId = ctx.Departments.Single(d => d.Name == "Engineering").Id;
        var spec = new EmployeeSearchSpec(new EmployeeSearchQuery { DepartmentId = engineeringId, SortBy = "joiningDate", SortDirection = "desc" });
        var page = await repo.PageAsync(spec, e => new { e.Name, Dept = e.Department!.Name }, new PageRequest(1, 2), CancellationToken.None);

        Assert.Equal(3, page.TotalCount);
        Assert.Equal(2, page.TotalPages);
        Assert.Equal(["Alice", "Bob"], page.Items.Select(i => i.Name));
        Assert.All(page.Items, i => Assert.Equal("Engineering", i.Dept));
    }

    [Fact]
    public async Task Sorts_through_navigation_property_from_allowlist()
    {
        await using var db = await SeededAsync();
        await using var ctx = db.CreateContext();
        var repo = new EfReadRepository<Employee>(ctx);

        var spec = new EmployeeSearchSpec(new EmployeeSearchQuery { SortBy = "department", SortDirection = "desc" });
        var names = await repo.ListAsync(spec, e => e.Name, 10, CancellationToken.None);

        Assert.Equal("Carol", names[0]);
    }

    [Fact]
    public async Task Unknown_sort_key_is_rejected_before_hitting_the_database()
    {
        await using var db = await SeededAsync();
        await using var ctx = db.CreateContext();
        var repo = new EfReadRepository<Employee>(ctx);

        var spec = new EmployeeSearchSpec(new EmployeeSearchQuery { SortBy = "Id); DROP TABLE Employee;--" });

        await Assert.ThrowsAsync<InvalidSortException>(() =>
            repo.PageAsync(spec, e => e.Name, new PageRequest(1, 10), CancellationToken.None));
    }
}

public class ProductSearchTests
{
    private static async Task<SqliteTestDb> SeededAsync(int count)
    {
        var db = await SqliteTestDb.CreateAsync();
        await using var ctx = db.CreateContext();
        var phones = new Category { Name = "Phones" };
        var laptops = new Category { Name = "Laptops" };
        var brand = new Brand { Name = "Acme" };
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < count; i++)
        {
            ctx.Products.Add(new Product
            {
                Name = $"Item {i:000}",
                Price = 10 + i,
                Category = i % 2 == 0 ? phones : laptops,
                Brand = brand,
                // Pairs share a timestamp so the Id tie-breaker in the cursor matters.
                CreatedAt = baseTime.AddMinutes(i / 2),
                IsActive = i != 7
            });
        }
        await ctx.SaveChangesAsync();
        return db;
    }

    [Fact]
    public async Task Keyset_feed_walks_every_active_row_exactly_once()
    {
        await using var db = await SeededAsync(25);
        var seen = new List<int>();
        string? cursor = null;
        var pages = 0;

        do
        {
            await using var ctx = db.CreateContext();
            var page = await new ProductSearchService(ctx).FeedAsync(
                new ProductSearchQuery { PageSize = 4, Cursor = cursor }, CancellationToken.None);
            seen.AddRange(page.Items.Select(i => i.Id));
            cursor = page.NextCursor;
            pages++;
        } while (cursor is not null && pages < 20);

        Assert.Equal(24, seen.Count);             // 25 minus one inactive
        Assert.Equal(seen.Count, seen.Distinct().Count());
        Assert.Equal(6, pages);                   // 24 rows / 4 per page, and no empty trailing page
    }

    [Fact]
    public async Task Offset_search_filters_by_categories_keyword_and_counts_total()
    {
        await using var db = await SeededAsync(25);
        await using var ctx = db.CreateContext();

        var result = await new ProductSearchService(ctx).SearchAsync(
            new ProductSearchQuery { CategoryIds = [1], Keyword = "Item 0", PageSize = 5, SortBy = "name" },
            CancellationToken.None);

        // Category 1 holds the even indexes 0..24 (13 products, all active); every name contains "Item 0".
        Assert.Equal(13, result.TotalCount);
        Assert.Equal(5, result.Items.Count);
        Assert.Equal("Item 000", result.Items[0].Name);
    }

    [Theory]
    [InlineData(null, 5, 1, null)]
    [InlineData("price", 101, null, "pageSize")]
    [InlineData("rating", 10, null, "sortBy")]
    [InlineData("name", 10, 0, "page")]
    public void Validation_rejects_bad_paging_and_sort_fields(string? sortBy, int pageSize, int? page, string? expectedError)
    {
        var errors = ProductSearchService.Validate(new ProductSearchQuery { SortBy = sortBy, PageSize = pageSize, Page = page });

        if (expectedError is null) Assert.Empty(errors);
        else Assert.Contains(expectedError, errors.Keys);
    }

    [Fact]
    public void Tampered_cursor_is_invalid()
    {
        Assert.False(ProductCursor.TryDecode("not-a-cursor!!", out _));
        Assert.True(ProductCursor.TryDecode(ProductCursor.Encode(DateTime.UtcNow, 42), out var c));
        Assert.Equal(42, c.Id);
    }
}
