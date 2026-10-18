using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

namespace AdPlay.Api.Common.Querying;

/// <summary>Thrown for client-supplied sort input that fails validation. Mapped to HTTP 400.</summary>
public sealed class InvalidSortException(string message) : ArgumentException(message);

/// <summary>
/// String-based sorting built from expression trees. EF Core translates the result to ORDER BY on real
/// columns; the input is only used to look up a property, so it never reaches SQL.
/// </summary>
public static class QueryableSortExtensions
{
    private const int MaxClauses = 3;
    private const int MaxLength = 200;

    private static readonly ConcurrentDictionary<(Type, string), PropertyInfo?> PropertyCache = new();

    /// <summary>
    /// Sorts by a spec such as <c>"Name"</c>, <c>"Salary desc"</c> or <c>"DepartmentId, Salary desc"</c>.
    /// Names are case-insensitive; nested paths like <c>"Department.Name"</c> are allowed.
    /// </summary>
    /// <param name="source">The query to sort.</param>
    /// <param name="orderBy">Comma-separated <c>Property [asc|desc]</c> clauses.</param>
    /// <param name="allowedProperties">Optional allowlist; pass one for client-facing input.</param>
    /// <exception cref="InvalidSortException">Empty or oversized input, unknown or non-scalar property, bad direction.</exception>
    public static IOrderedQueryable<T> OrderBy<T>(
        this IQueryable<T> source,
        string orderBy,
        IEnumerable<string>? allowedProperties = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (string.IsNullOrWhiteSpace(orderBy))
            throw new InvalidSortException("Sort expression is required.");
        if (orderBy.Length > MaxLength)
            throw new InvalidSortException($"Sort expression must be at most {MaxLength} characters.");

        var clauses = orderBy.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (clauses.Length == 0)
            throw new InvalidSortException("Sort expression is required.");
        if (clauses.Length > MaxClauses)
            throw new InvalidSortException($"At most {MaxClauses} sort fields are allowed.");

        var allowed = allowedProperties is null
            ? null
            : new HashSet<string>(allowedProperties, StringComparer.OrdinalIgnoreCase);

        var expression = source.Expression;
        for (var i = 0; i < clauses.Length; i++)
        {
            var (path, descending) = ParseClause(clauses[i]);

            if (allowed is not null && !allowed.Contains(path))
                throw new InvalidSortException($"Sorting by '{path}' is not allowed.");

            var parameter = Expression.Parameter(typeof(T), "x");
            var body = BuildMemberAccess(parameter, path);
            var keySelector = Expression.Lambda(body, parameter);

            var method = (i == 0, descending) switch
            {
                (true, false) => nameof(Queryable.OrderBy),
                (true, true) => nameof(Queryable.OrderByDescending),
                (false, false) => nameof(Queryable.ThenBy),
                (false, true) => nameof(Queryable.ThenByDescending)
            };

            expression = Expression.Call(
                typeof(Queryable), method, [typeof(T), body.Type],
                expression, Expression.Quote(keySelector));
        }

        return (IOrderedQueryable<T>)source.Provider.CreateQuery<T>(expression);
    }

    private static (string Path, bool Descending) ParseClause(string clause)
    {
        var parts = clause.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var descending = parts.Length switch
        {
            1 => false,
            2 when parts[1].Equals("asc", StringComparison.OrdinalIgnoreCase) => false,
            2 when parts[1].Equals("desc", StringComparison.OrdinalIgnoreCase) => true,
            2 => throw new InvalidSortException($"Invalid sort direction '{parts[1]}'. Use 'asc' or 'desc'."),
            _ => throw new InvalidSortException($"Invalid sort clause '{clause}'.")
        };
        return (parts[0], descending);
    }

    private static Expression BuildMemberAccess(ParameterExpression parameter, string path)
    {
        Expression current = parameter;
        foreach (var segment in path.Split('.'))
        {
            var property = PropertyCache.GetOrAdd((current.Type, segment), static key =>
                key.Item1.GetProperty(
                    key.Item2,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase));

            if (property is null || !property.CanRead || property.GetIndexParameters().Length > 0)
                throw new InvalidSortException($"Unknown sort field '{path}'.");

            current = Expression.Property(current, property);
        }

        if (!IsSortable(current.Type))
            throw new InvalidSortException($"Field '{path}' cannot be used for sorting.");

        return current;
    }

    // Only scalars: navigations and collections can't be translated to ORDER BY.
    private static bool IsSortable(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        return t.IsPrimitive || t.IsEnum
            || t == typeof(string) || t == typeof(decimal) || t == typeof(Guid)
            || t == typeof(DateTime) || t == typeof(DateTimeOffset)
            || t == typeof(DateOnly) || t == typeof(TimeOnly) || t == typeof(TimeSpan);
    }
}
