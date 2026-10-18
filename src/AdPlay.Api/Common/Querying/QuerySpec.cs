using System.Linq.Expressions;

namespace AdPlay.Api.Common.Querying;

/// <summary>
/// A server-authored query shape. Clients supply values (filters, a sort key from <see cref="SortMap"/>),
/// never expressions, so every query that can run is known and indexable.
/// </summary>
public abstract class QuerySpec<T> where T : class
{
    private readonly List<Expression<Func<T, bool>>> _criteria = [];
    private readonly List<string> _includes = [];

    public IReadOnlyList<Expression<Func<T, bool>>> Criteria => _criteria;
    public IReadOnlyList<string> Includes => _includes;

    /// <summary>Public sort key → entity property path. Anything not listed is rejected.</summary>
    protected abstract IReadOnlyDictionary<string, string> SortMap { get; }

    /// <summary>Public sort key used when the client sends none.</summary>
    protected abstract string DefaultSort { get; }

    /// <summary>Unique column appended to every ORDER BY so paging is deterministic.</summary>
    protected virtual string TieBreaker => "Id";

    public string? SortBy { get; init; }
    public bool Descending { get; init; }
    public bool AsSplitQuery { get; protected set; }

    protected void Where(Expression<Func<T, bool>> predicate) => _criteria.Add(predicate);

    /// <summary>Only applied when returning entities; projections build their own joins.</summary>
    protected void Include(string navigationPath) => _includes.Add(navigationPath);

    /// <summary>Maps the client sort key to a validated spec for <see cref="QueryableSortExtensions"/>.</summary>
    public string ResolveOrderBy()
    {
        var key = string.IsNullOrWhiteSpace(SortBy) ? DefaultSort : SortBy.Trim();
        var match = SortMap.FirstOrDefault(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (match.Key is null)
            throw new InvalidSortException(
                $"Invalid sort field '{key}'. Allowed: {string.Join(", ", SortMap.Keys)}.");

        var direction = Descending ? "desc" : "asc";
        return match.Value.Equals(TieBreaker, StringComparison.OrdinalIgnoreCase)
            ? $"{match.Value} {direction}"
            : $"{match.Value} {direction}, {TieBreaker} {direction}";
    }
}
