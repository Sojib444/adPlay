using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;

namespace AdPlay.Api.Data;

/// <summary>Recognises unique-key violations (an expected outcome under concurrency) without tying services to a provider.</summary>
public interface IDbErrorClassifier
{
    bool IsUniqueViolation(DbUpdateException ex);
}

public sealed class MySqlDbErrorClassifier : IDbErrorClassifier
{
    private const int ErDupEntry = 1062;

    public bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is MySqlException { Number: ErDupEntry };
}
