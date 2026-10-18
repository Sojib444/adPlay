using System.Text.RegularExpressions;
using AdPlay.Api.Data;
using AdPlay.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace AdPlay.Api.Features.Subscriptions;

public enum SubscribeOutcome
{
    Subscribed,
    AlreadySubscribed,
    UserNotFound
}

/// <summary>
/// Subscribes a user exactly once under any concurrency. The check and the write are a single conditional
/// UPDATE, so only one caller can see "1 row affected"; UNIQUE(Subscriptions.UserId) backs it up.
/// </summary>
public sealed partial class SubscriptionService(AppDbContext db, IDbErrorClassifier dbErrors, TimeProvider clock)
{
    public static bool IsValidMobile(string? mobile) => mobile is not null && MobilePattern().IsMatch(mobile);

    public async Task<SubscribeOutcome> SubscribeAsync(string mobile, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var affected = await db.Users
            .Where(u => u.Mobile == mobile && !u.IsSubscribed)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.IsSubscribed, true)
                .SetProperty(u => u.SubscribedAt, now), ct);

        if (affected == 0)
        {
            await tx.RollbackAsync(ct);
            var exists = await db.Users.AnyAsync(u => u.Mobile == mobile, ct);
            return exists ? SubscribeOutcome.AlreadySubscribed : SubscribeOutcome.UserNotFound;
        }

        var userId = await db.Users.Where(u => u.Mobile == mobile).Select(u => u.Id).SingleAsync(ct);
        db.Subscriptions.Add(new Subscription { UserId = userId, CreatedAt = now });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (dbErrors.IsUniqueViolation(ex))
        {
            // Only reachable if another code path flipped the flag without the guard above.
            await tx.RollbackAsync(ct);
            return SubscribeOutcome.AlreadySubscribed;
        }

        await tx.CommitAsync(ct);
        return SubscribeOutcome.Subscribed;
    }

    [GeneratedRegex(@"^\+?[0-9]{10,15}$")]
    private static partial Regex MobilePattern();
}
