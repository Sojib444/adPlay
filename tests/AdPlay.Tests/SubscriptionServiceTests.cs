using AdPlay.Api.Domain;
using AdPlay.Api.Features.Subscriptions;
using AdPlay.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AdPlay.Tests;

public class SubscriptionServiceTests
{
    private const string Mobile = "8801700000001";

    [Fact]
    public async Task Concurrent_requests_subscribe_exactly_once()
    {
        await using var db = await SqliteTestDb.CreateAsync();
        await using (var seed = db.CreateContext())
        {
            seed.Users.Add(new User { Mobile = Mobile });
            await seed.SaveChangesAsync();
        }

        // Each task is an independent "request" with its own DbContext/connection.
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 50).Select(async _ =>
        {
            await using var ctx = db.CreateContext();
            var service = new SubscriptionService(ctx, new SqliteDbErrorClassifier(), TimeProvider.System);
            return await service.SubscribeAsync(Mobile, CancellationToken.None);
        }));

        Assert.Single(outcomes, o => o == SubscribeOutcome.Subscribed);
        Assert.Equal(49, outcomes.Count(o => o == SubscribeOutcome.AlreadySubscribed));

        await using var verify = db.CreateContext();
        Assert.Equal(1, await verify.Subscriptions.CountAsync());
        Assert.True((await verify.Users.SingleAsync()).IsSubscribed);
    }

    [Fact]
    public async Task Unknown_mobile_is_reported_as_not_found()
    {
        await using var db = await SqliteTestDb.CreateAsync();
        await using var ctx = db.CreateContext();
        var service = new SubscriptionService(ctx, new SqliteDbErrorClassifier(), TimeProvider.System);

        Assert.Equal(SubscribeOutcome.UserNotFound, await service.SubscribeAsync("8801999999999", CancellationToken.None));
    }
}
