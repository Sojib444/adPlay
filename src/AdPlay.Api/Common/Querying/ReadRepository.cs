using System.Linq.Expressions;
using AdPlay.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AdPlay.Api.Common.Querying;

public interface IReadRepository<T> where T : class
{
    /// <summary>Filtered, sorted page projected to <typeparamref name="TResult"/>, plus a total count.</summary>
    Task<PagedResult<TResult>> PageAsync<TResult>(
        QuerySpec<T> spec, Expression<Func<T, TResult>> projection, PageRequest page, CancellationToken ct);

    Task<IReadOnlyList<TResult>> ListAsync<TResult>(
        QuerySpec<T> spec, Expression<Func<T, TResult>> projection, int maxItems, CancellationToken ct);

    /// <summary>Returns untracked entities with the spec's includes. Prefer projections for read APIs.</summary>
    Task<IReadOnlyList<T>> ListEntitiesAsync(QuerySpec<T> spec, int maxItems, CancellationToken ct);

    Task<long> CountAsync(QuerySpec<T> spec, CancellationToken ct);
}

/// <summary>
/// Read-only, no-tracking queries. Projections select only the columns they need, counts skip ORDER BY and
/// projection, and every result size is bounded.
/// </summary>
public sealed class EfReadRepository<T>(AppDbContext db) : IReadRepository<T> where T : class
{
    public async Task<PagedResult<TResult>> PageAsync<TResult>(
        QuerySpec<T> spec, Expression<Func<T, TResult>> projection, PageRequest page, CancellationToken ct)
    {
        var filtered = ApplyCriteria(spec);

        var total = await filtered.LongCountAsync(ct);
        if (total == 0 || page.Skip >= total)
            return new PagedResult<TResult>([], page.Page, page.PageSize, total);

        var items = await filtered
            .OrderBy(spec.ResolveOrderBy())
            .Skip(page.Skip)
            .Take(page.PageSize)
            .Select(projection)
            .ToListAsync(ct);

        return new PagedResult<TResult>(items, page.Page, page.PageSize, total);
    }

    public async Task<IReadOnlyList<TResult>> ListAsync<TResult>(
        QuerySpec<T> spec, Expression<Func<T, TResult>> projection, int maxItems, CancellationToken ct) =>
        await ApplyCriteria(spec)
            .OrderBy(spec.ResolveOrderBy())
            .Take(Math.Clamp(maxItems, 1, 1000))
            .Select(projection)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<T>> ListEntitiesAsync(QuerySpec<T> spec, int maxItems, CancellationToken ct)
    {
        var query = ApplyCriteria(spec);
        query = spec.Includes.Aggregate(query, (q, path) => q.Include(path));
        if (spec.AsSplitQuery) query = query.AsSplitQuery();

        return await query
            .OrderBy(spec.ResolveOrderBy())
            .Take(Math.Clamp(maxItems, 1, 1000))
            .ToListAsync(ct);
    }

    public Task<long> CountAsync(QuerySpec<T> spec, CancellationToken ct) =>
        ApplyCriteria(spec).LongCountAsync(ct);

    private IQueryable<T> ApplyCriteria(QuerySpec<T> spec) =>
        spec.Criteria.Aggregate(db.Set<T>().AsNoTracking(), (q, c) => q.Where(c));
}
