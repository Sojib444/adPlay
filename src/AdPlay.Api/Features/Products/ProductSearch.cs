using System.Buffers.Text;
using System.Globalization;
using System.Linq.Expressions;
using System.Text;
using AdPlay.Api.Common;
using AdPlay.Api.Data;
using AdPlay.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace AdPlay.Api.Features.Products;

public sealed class ProductSearchQuery
{
    public string? Keyword { get; init; }
    public decimal? MinPrice { get; init; }
    public decimal? MaxPrice { get; init; }
    public int[]? CategoryIds { get; init; }
    public int[]? BrandIds { get; init; }
    public string? SortBy { get; init; }
    public string? SortDirection { get; init; }
    public int? Page { get; init; }
    public int? PageSize { get; init; }
    /// <summary>Keyset endpoint only: opaque cursor returned by the previous page.</summary>
    public string? Cursor { get; init; }
}

public sealed record ProductListItem(int Id, string Name, decimal Price, string Category, string Brand, DateTime CreatedAt);

/// <summary>Product search with offset paging (page numbers) and keyset paging (infinite scroll).</summary>
public sealed class ProductSearchService(AppDbContext db)
{
    public const int MaxKeywordLength = 100;
    public const int MaxFilterIds = 20;
    public static readonly string[] SortFields = ["name", "price", "newest"];

    public static Dictionary<string, string[]> Validate(ProductSearchQuery q)
    {
        var errors = new Dictionary<string, string[]>();
        if (q.Keyword is { Length: > MaxKeywordLength })
            errors["keyword"] = [$"Keyword must be at most {MaxKeywordLength} characters."];
        if (q.MinPrice < 0 || q.MaxPrice < 0)
            errors["price"] = ["Prices cannot be negative."];
        if (q.MinPrice > q.MaxPrice)
            errors["price"] = ["minPrice cannot be greater than maxPrice."];
        if (q.CategoryIds?.Length > MaxFilterIds)
            errors["categoryIds"] = [$"At most {MaxFilterIds} categories."];
        if (q.BrandIds?.Length > MaxFilterIds)
            errors["brandIds"] = [$"At most {MaxFilterIds} brands."];
        if (q.SortBy is not null && !SortFields.Contains(q.SortBy, StringComparer.OrdinalIgnoreCase))
            errors["sortBy"] = [$"Allowed values: {string.Join(", ", SortFields)}."];
        if (q.SortDirection is not (null or "asc" or "desc"))
            errors["sortDirection"] = ["Allowed values: asc, desc."];
        if (q.PageSize is < 1 or > PageRequest.MaxPageSize)
            errors["pageSize"] = [$"pageSize must be between 1 and {PageRequest.MaxPageSize}."];
        if (q.Page is < 1)
            errors["page"] = ["page must be 1 or greater."];
        if (q.Cursor is not null && !ProductCursor.TryDecode(q.Cursor, out _))
            errors["cursor"] = ["Invalid cursor."];
        return errors;
    }

    /// <summary>Offset paging with a total count; fine for the first pages people actually visit.</summary>
    public async Task<PagedResult<ProductListItem>> SearchAsync(ProductSearchQuery q, CancellationToken ct)
    {
        var page = new PageRequest(q.Page, q.PageSize);
        var filtered = ApplyFilters(db.Products.AsNoTracking(), q);

        // COUNT runs on the bare filtered query: no ORDER BY, no JOINs from the projection.
        var total = await filtered.CountAsync(ct);
        if (total == 0 || page.Skip >= total)
            return new PagedResult<ProductListItem>([], page.Page, page.PageSize, total);

        var items = await ApplySort(filtered, q)
            .Skip(page.Skip)
            .Take(page.PageSize)
            .Select(Projection)
            .ToListAsync(ct);

        return new PagedResult<ProductListItem>(items, page.Page, page.PageSize, total);
    }

    /// <summary>Keyset paging on (CreatedAt DESC, Id DESC): every page costs the same as the first.</summary>
    public async Task<CursorPage<ProductListItem>> FeedAsync(ProductSearchQuery q, CancellationToken ct)
    {
        var size = new PageRequest(1, q.PageSize).PageSize;
        var query = ApplyFilters(db.Products.AsNoTracking(), q);

        if (q.Cursor is not null && ProductCursor.TryDecode(q.Cursor, out var c))
            query = query.Where(p => p.CreatedAt < c.CreatedAt || (p.CreatedAt == c.CreatedAt && p.Id < c.Id));

        var rows = await query
            .OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id)
            .Take(size + 1) // one extra row tells us whether another page exists, without a COUNT
            .Select(Projection)
            .ToListAsync(ct);

        var hasMore = rows.Count > size;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        var next = hasMore ? ProductCursor.Encode(rows[^1].CreatedAt, rows[^1].Id) : null;
        return new CursorPage<ProductListItem>(rows, next, hasMore);
    }

    private static readonly Expression<Func<Product, ProductListItem>> Projection =
        p => new ProductListItem(p.Id, p.Name, p.Price, p.Category!.Name, p.Brand!.Name, p.CreatedAt);

    private static IQueryable<Product> ApplyFilters(IQueryable<Product> query, ProductSearchQuery q)
    {
        query = query.Where(p => p.IsActive);

        if (!string.IsNullOrWhiteSpace(q.Keyword))
        {
            // Bound as a parameter. At catalogue scale, switch to a FULLTEXT index or a search engine.
            var keyword = q.Keyword.Trim();
            query = query.Where(p => p.Name.Contains(keyword));
        }
        if (q.MinPrice is { } min) query = query.Where(p => p.Price >= min);
        if (q.MaxPrice is { } max) query = query.Where(p => p.Price <= max);
        if (q.CategoryIds is { Length: > 0 } categories)
        {
            var ids = categories.Distinct().ToArray();
            query = query.Where(p => ids.Contains(p.CategoryId)); // IN (@p0, @p1, ...)
        }
        if (q.BrandIds is { Length: > 0 } brands)
        {
            var ids = brands.Distinct().ToArray();
            query = query.Where(p => ids.Contains(p.BrandId));
        }
        return query;
    }

    // Allowlisted keys map to typed expressions. Id is the final tie-breaker so pages never overlap.
    private static IQueryable<Product> ApplySort(IQueryable<Product> query, ProductSearchQuery q)
    {
        var desc = q.SortDirection == "desc";
        return (q.SortBy?.ToLowerInvariant() ?? "newest") switch
        {
            "name" => desc ? query.OrderByDescending(p => p.Name).ThenByDescending(p => p.Id)
                           : query.OrderBy(p => p.Name).ThenBy(p => p.Id),
            "price" => desc ? query.OrderByDescending(p => p.Price).ThenByDescending(p => p.Id)
                            : query.OrderBy(p => p.Price).ThenBy(p => p.Id),
            _ => q.SortDirection == "asc"
                ? query.OrderBy(p => p.CreatedAt).ThenBy(p => p.Id)
                : query.OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id)
        };
    }
}

/// <summary>Opaque, URL-safe cursor; clients must not build or parse it.</summary>
public static class ProductCursor
{
    public static string Encode(DateTime createdAt, int id) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{createdAt.Ticks}:{id}"));

    public static bool TryDecode(string cursor, out (DateTime CreatedAt, int Id) value)
    {
        value = default;
        if (cursor.Length > 100) return false;
        try
        {
            var parts = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor)).Split(':');
            if (parts.Length == 2
                && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                && ticks <= DateTime.MaxValue.Ticks
                && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                value = (new DateTime(ticks, DateTimeKind.Utc), id);
                return true;
            }
        }
        catch (FormatException) { }
        return false;
    }
}
