using AdPlay.Api.Domain;
using AdPlay.Api.Features.Payments;
using AdPlay.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AdPlay.Tests;

public class PaymentServiceTests
{
    private const string Merchant = "m_1";

    private sealed class StubProvider : IPaymentProviderClient
    {
        private int _charges;
        public int Charges => _charges;
        public Func<ProviderChargeRequest, CancellationToken, Task<ProviderChargeResult>> OnCharge { get; set; } =
            (r, _) => Task.FromResult(new ProviderChargeResult(ProviderOutcome.Approved, "prov_" + r.IdempotencyKey[..6], null));
        public ProviderChargeResult StatusResult { get; set; } = new(ProviderOutcome.Approved, "prov_rec", null);

        public Task<ProviderChargeResult> ChargeAsync(ProviderChargeRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref _charges);
            return OnCharge(request, ct);
        }

        public Task<ProviderChargeResult> GetStatusAsync(string idempotencyKey, CancellationToken ct) =>
            Task.FromResult(StatusResult);
    }

    private static PaymentService CreateService(SqliteTestDb db, StubProvider provider, PaymentOptions? options = null) =>
        new(db.CreateContext(), provider, new SqliteDbErrorClassifier(),
            Options.Create(options ?? new PaymentOptions()), TimeProvider.System, NullLogger<PaymentService>.Instance);

    private static CreatePaymentRequest Request(string orderId = "ORD-1", decimal amount = 100m) =>
        new(orderId, amount, "BDT", "tok_visa");

    [Fact]
    public async Task Approved_payment_succeeds_and_replay_returns_same_payment_without_charging_again()
    {
        await using var db = await SqliteTestDb.CreateAsync();
        var provider = new StubProvider();

        var first = await CreateService(db, provider).CreateAsync(Merchant, "key-1", Request(), CancellationToken.None);
        var replay = await CreateService(db, provider).CreateAsync(Merchant, "key-1", Request(), CancellationToken.None);

        Assert.Equal(CreatePaymentResultKind.Created, first.Kind);
        Assert.Equal(PaymentStatus.Succeeded, first.Payment!.Status);
        Assert.Equal(CreatePaymentResultKind.Replayed, replay.Kind);
        Assert.Equal(first.Payment.Id, replay.Payment!.Id);
        Assert.Equal(1, provider.Charges);
    }

    [Fact]
    public async Task Same_key_with_different_payload_is_rejected()
    {
        await using var db = await SqliteTestDb.CreateAsync();
        var provider = new StubProvider();
        await CreateService(db, provider).CreateAsync(Merchant, "key-1", Request(amount: 100), CancellationToken.None);

        var result = await CreateService(db, provider).CreateAsync(Merchant, "key-1", Request(amount: 999), CancellationToken.None);

        Assert.Equal(CreatePaymentResultKind.KeyReusedWithDifferentPayload, result.Kind);
        Assert.Equal(1, provider.Charges);
    }

    [Fact]
    public async Task Second_payment_for_a_paid_order_is_blocked_even_with_a_new_key()
    {
        await using var db = await SqliteTestDb.CreateAsync();
        var provider = new StubProvider();
        await CreateService(db, provider).CreateAsync(Merchant, "key-1", Request(), CancellationToken.None);

        var result = await CreateService(db, provider).CreateAsync(Merchant, "key-2", Request(), CancellationToken.None);

        Assert.Equal(CreatePaymentResultKind.DuplicateOrder, result.Kind);
        Assert.Equal(1, provider.Charges);
    }

    [Fact]
    public async Task Declined_payment_releases_the_order_for_a_new_attempt()
    {
        await using var db = await SqliteTestDb.CreateAsync();
        var provider = new StubProvider
        {
            OnCharge = (_, _) => Task.FromResult(new ProviderChargeResult(ProviderOutcome.Declined, null, "insufficient_funds"))
        };

        var declined = await CreateService(db, provider).CreateAsync(Merchant, "key-1", Request(), CancellationToken.None);
        Assert.Equal(PaymentStatus.Failed, declined.Payment!.Status);
        Assert.Null(declined.Payment.ActiveOrderId);

        provider.OnCharge = (r, _) => Task.FromResult(new ProviderChargeResult(ProviderOutcome.Approved, "prov_ok", null));
        var retry = await CreateService(db, provider).CreateAsync(Merchant, "key-2", Request(), CancellationToken.None);

        Assert.Equal(CreatePaymentResultKind.Created, retry.Kind);
        Assert.Equal(PaymentStatus.Succeeded, retry.Payment!.Status);
    }

    [Fact]
    public async Task Provider_timeout_is_recorded_as_unknown_and_settled_by_reconciliation()
    {
        await using var db = await SqliteTestDb.CreateAsync();
        var provider = new StubProvider
        {
            OnCharge = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return null!; }
        };
        var options = new PaymentOptions { ProviderTimeout = TimeSpan.FromMilliseconds(200) };

        var result = await CreateService(db, provider, options).CreateAsync(Merchant, "key-1", Request(), CancellationToken.None);
        Assert.Equal(PaymentStatus.Unknown, result.Payment!.Status);
        Assert.NotNull(result.Payment.ActiveOrderId); // order stays locked while the outcome is unknown

        var settled = await CreateService(db, provider, options).ReconcileAsync(10, CancellationToken.None);

        Assert.Equal(1, settled);
        await using var ctx = db.CreateContext();
        Assert.Equal(PaymentStatus.Succeeded, (await ctx.Payments.SingleAsync()).Status);
    }

    [Fact]
    public async Task Client_cancellation_after_submission_does_not_lose_the_provider_result()
    {
        await using var db = await SqliteTestDb.CreateAsync();
        using var clientGoesAway = new CancellationTokenSource();
        var provider = new StubProvider
        {
            OnCharge = async (r, ct) =>
            {
                clientGoesAway.Cancel();          // the HTTP client disconnects mid-call
                await Task.Delay(50, ct);
                return new ProviderChargeResult(ProviderOutcome.Approved, "prov_ok", null);
            }
        };

        var result = await CreateService(db, provider).CreateAsync(Merchant, "key-1", Request(), clientGoesAway.Token);

        Assert.Equal(PaymentStatus.Succeeded, result.Payment!.Status);
    }

    [Fact]
    public async Task Concurrent_requests_with_the_same_key_create_one_payment_and_one_charge()
    {
        await using var db = await SqliteTestDb.CreateAsync();
        var provider = new StubProvider
        {
            OnCharge = async (r, ct) =>
            {
                await Task.Delay(100, ct);
                return new ProviderChargeResult(ProviderOutcome.Approved, "prov_ok", null);
            }
        };

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            CreateService(db, provider).CreateAsync(Merchant, "key-1", Request(), CancellationToken.None)));

        Assert.Single(results, r => r.Kind == CreatePaymentResultKind.Created);
        Assert.All(results.Where(r => r.Kind != CreatePaymentResultKind.Created),
            r => Assert.Contains(r.Kind, new[] { CreatePaymentResultKind.InProgress, CreatePaymentResultKind.Replayed }));
        Assert.Equal(1, provider.Charges);
        await using var ctx = db.CreateContext();
        Assert.Equal(1, await ctx.Payments.CountAsync());
    }

    [Fact]
    public async Task Webhooks_are_deduplicated_and_settle_unknown_payments()
    {
        await using var db = await SqliteTestDb.CreateAsync();
        var provider = new StubProvider
        {
            OnCharge = (_, _) => Task.FromResult(ProviderChargeResult.Unknown("provider_unreachable"))
        };
        var payment = (await CreateService(db, provider).CreateAsync(Merchant, "key-1", Request(), CancellationToken.None)).Payment!;
        Assert.Equal(PaymentStatus.Unknown, payment.Status);

        var evt = new ProviderWebhookEvent("evt_1", "payment.succeeded", payment.Id.ToString("N"), "prov_123", null);
        var first = await CreateService(db, provider).HandleWebhookAsync("demo", evt, CancellationToken.None);
        var again = await CreateService(db, provider).HandleWebhookAsync("demo", evt, CancellationToken.None);

        Assert.Equal(WebhookResult.Processed, first);
        Assert.Equal(WebhookResult.Duplicate, again);
        await using var ctx = db.CreateContext();
        var stored = await ctx.Payments.SingleAsync();
        Assert.Equal(PaymentStatus.Succeeded, stored.Status);
        Assert.Equal("prov_123", stored.ProviderReference);
        Assert.Equal(1, await ctx.WebhookEvents.CountAsync());
    }

    [Fact]
    public async Task Late_failure_webhook_cannot_move_a_succeeded_payment_backwards()
    {
        await using var db = await SqliteTestDb.CreateAsync();
        var provider = new StubProvider();
        var payment = (await CreateService(db, provider).CreateAsync(Merchant, "key-1", Request(), CancellationToken.None)).Payment!;

        var evt = new ProviderWebhookEvent("evt_9", "payment.failed", payment.Id.ToString("N"), null, "late");
        await CreateService(db, provider).HandleWebhookAsync("demo", evt, CancellationToken.None);

        await using var ctx = db.CreateContext();
        Assert.Equal(PaymentStatus.Succeeded, (await ctx.Payments.SingleAsync()).Status);
    }
}
