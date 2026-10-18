using AdPlay.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AdPlay.Tests.Infrastructure;

/// <summary>
/// File-backed SQLite database so several DbContexts (one per simulated request) can hit it concurrently.
/// SQLite serialises writers, which is enough to prove the conditional-UPDATE / unique-index logic; MySQL-specific
/// behaviour (InnoDB row locks, error 1062) is covered by MySqlDbErrorClassifier and the SQL scripts.
/// </summary>
public sealed class SqliteTestDb : IAsyncDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"adplay-tests-{Guid.NewGuid():N}.db");

    private string ConnectionString => $"Data Source={_path};Pooling=False;Default Timeout=30";

    public static async Task<SqliteTestDb> CreateAsync()
    {
        var db = new SqliteTestDb();
        await using var ctx = db.CreateContext();
        await ctx.Database.EnsureCreatedAsync();
        return db;
    }

    public AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(ConnectionString).Options);

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_path); } catch (IOException) { /* best effort */ }
        return ValueTask.CompletedTask;
    }
}

public sealed class SqliteDbErrorClassifier : IDbErrorClassifier
{
    private const int SqliteConstraint = 19;

    public bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is SqliteException { SqliteErrorCode: SqliteConstraint };
}
