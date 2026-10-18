using AdPlay.Api.Data;
using AdPlay.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AdPlay.Api.Features.Payments;

public enum CreatePaymentResultKind
{
    /// <summary>This call created the payment and drove it as far as the provider allowed.</summary>
    Created,
    /// <summary>Same key and payload as an earlier call; the stored result is returned.</summary>
    Replayed,
    KeyReusedWithDifferentPayload,
    /// <summary>Same key; the first request is still talking to the provider.</summary>
    InProgress,
    /// <summary>Different key, but the order already has a live payment.</summary>
    DuplicateOrder
}

public sealed record CreatePaymentResult(CreatePaymentResultKind Kind, Payment? Payment);

public enum WebhookResult { Processed, Duplicate, Ignored, PaymentNotFound }

public sealed record ProviderWebhookEvent(string Id, string Type, string? PaymentId, string? ProviderReference, string? Reason);

/// <summary>
/// Idempotent payment creation. Invariants live in the database: UNIQUE(MerchantId, IdempotencyKey),
/// UNIQUE(MerchantId, ActiveOrderId), and conditional UPDATEs for every state change, so the request,
/// webhooks and the reconciler can race without ever moving a payment backwards.
/// No transaction or lock is held across the provider call.
/// </summary>
public sealed partial class PaymentService(
    AppDbContext db,
    IPaymentProviderClient provider,
    IDbErrorClassifier dbErrors,
    IOptions<PaymentOptions> options,
    TimeProvider clock,
    ILogger<PaymentService> logger)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<CreatePaymentResult> CreateAsync(
        string merchantId, string idempotencyKey, CreatePaymentRequest request, CancellationToken ct)
    {
        var hash = RequestHasher.Hash(request);

        var existing = await FindByKeyAsync(merchantId, idempotencyKey, ct);
        if (existing is not null)
            return Classify(existing, hash);

        var now = Now;
        var payment = new Payment
        {
            Id = Guid.CreateVersion7(),
            MerchantId = merchantId,
            IdempotencyKey = idempotencyKey,
            RequestHash = hash,
            OrderId = request.OrderId,
            ActiveOrderId = request.OrderId,
            Amount = decimal.Round(request.Amount, 2),
            Currency = request.Currency.ToUpperInvariant(),
            Status = PaymentStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Payments.Add(payment);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (dbErrors.IsUniqueViolation(ex))
        {
            // A concurrent request won the insert; answer the way that request would.
            db.ChangeTracker.Clear();
            existing = await FindByKeyAsync(merchantId, idempotencyKey, ct);
            if (existing is not null)
                return Classify(existing, hash);

            var live = await db.Payments.AsNoTracking()
                .FirstOrDefaultAsync(p => p.MerchantId == merchantId && p.ActiveOrderId == request.OrderId, ct);
            return new CreatePaymentResult(CreatePaymentResultKind.DuplicateOrder, live);
        }
        finally
        {
            db.ChangeTracker.Clear();
        }

        // The client's token is deliberately not passed on: once money may move, the outcome must be recorded
        // even if the caller disconnects. The provider call has its own timeout.
        var final = await ChargeAsync(payment, request.PaymentMethodToken);
        return new CreatePaymentResult(CreatePaymentResultKind.Created, final);
    }

    public Task<Payment?> GetAsync(string merchantId, Guid id, CancellationToken ct) =>
        db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id && p.MerchantId == merchantId, ct);

    private async Task<Payment> ChargeAsync(Payment payment, string paymentMethodToken)
    {
        var claimed = await db.Payments
            .Where(p => p.Id == payment.Id && p.Status == PaymentStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, PaymentStatus.Processing)
                .SetProperty(p => p.Attempts, p => p.Attempts + 1)
                .SetProperty(p => p.UpdatedAt, Now));
        if (claimed == 0)
            return await LoadAsync(payment.Id);

        ProviderChargeResult result;
        using var timeout = new CancellationTokenSource(options.Value.ProviderTimeout);
        try
        {
            // Our payment id doubles as the provider-side idempotency key.
            result = await provider.ChargeAsync(
                new ProviderChargeRequest(payment.Id.ToString("N"), payment.OrderId, payment.Amount, payment.Currency, paymentMethodToken),
                timeout.Token);
        }
        catch (OperationCanceledException)
        {
            result = ProviderChargeResult.Unknown("provider_timeout");
        }
        catch (HttpRequestException ex)
        {
            // A dropped connection is as ambiguous as a timeout: never assume "not charged".
            LogProviderCallFailed(ex, payment.Id);
            result = ProviderChargeResult.Unknown("provider_unreachable");
        }

        await ApplyProviderResultAsync(payment.Id, result, CancellationToken.None);
        return await LoadAsync(payment.Id);
    }

    /// <summary>Applies a provider verdict if the payment is still open. Returns false if it was already settled.</summary>
    public async Task<bool> ApplyProviderResultAsync(Guid paymentId, ProviderChargeResult result, CancellationToken ct)
    {
        var now = Now;
        var open = db.Payments.Where(p => p.Id == paymentId &&
            (p.Status == PaymentStatus.Pending || p.Status == PaymentStatus.Processing || p.Status == PaymentStatus.Unknown));

        var affected = result.Outcome switch
        {
            ProviderOutcome.Approved => await open.ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, PaymentStatus.Succeeded)
                .SetProperty(p => p.ProviderReference, result.ProviderReference)
                .SetProperty(p => p.FailureReason, (string?)null)
                .SetProperty(p => p.UpdatedAt, now), ct),

            ProviderOutcome.Declined => await open.ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, PaymentStatus.Failed)
                .SetProperty(p => p.ActiveOrderId, (string?)null) // frees the order for a new attempt
                .SetProperty(p => p.FailureReason, result.Reason)
                .SetProperty(p => p.UpdatedAt, now), ct),

            // Ambiguous: keep the order locked until reconciliation or a webhook settles it.
            _ => await db.Payments
                .Where(p => p.Id == paymentId && p.Status == PaymentStatus.Processing)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.Status, PaymentStatus.Unknown)
                    .SetProperty(p => p.FailureReason, result.Reason)
                    .SetProperty(p => p.UpdatedAt, now), ct)
        };

        return affected == 1;
    }

    /// <summary>
    /// The event row and the state change commit together, and UNIQUE(Provider, ProviderEventId) turns
    /// redelivery into a no-op.
    /// </summary>
    public async Task<WebhookResult> HandleWebhookAsync(string providerName, ProviderWebhookEvent evt, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var record = new WebhookEvent
        {
            Provider = providerName,
            ProviderEventId = evt.Id,
            EventType = evt.Type,
            ProviderReference = evt.ProviderReference,
            ReceivedAt = Now
        };
        db.WebhookEvents.Add(record);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (dbErrors.IsUniqueViolation(ex))
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            return WebhookResult.Duplicate;
        }

        var outcome = await ApplyWebhookAsync(evt, ct);

        record.ProcessedAt = Now;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return outcome;
    }

    private async Task<WebhookResult> ApplyWebhookAsync(ProviderWebhookEvent evt, CancellationToken ct)
    {
        ProviderChargeResult? verdict = evt.Type switch
        {
            "payment.succeeded" => new ProviderChargeResult(ProviderOutcome.Approved, evt.ProviderReference, null),
            "payment.failed" => new ProviderChargeResult(ProviderOutcome.Declined, evt.ProviderReference, evt.Reason ?? "declined"),
            _ => null
        };
        if (verdict is null)
            return WebhookResult.Ignored;

        if (!Guid.TryParseExact(evt.PaymentId, "N", out var paymentId) ||
            !await db.Payments.AnyAsync(p => p.Id == paymentId, ct))
            return WebhookResult.PaymentNotFound;

        if (!await ApplyProviderResultAsync(paymentId, verdict, ct))
        {
            var current = await LoadAsync(paymentId);
            var expected = verdict.Outcome == ProviderOutcome.Approved ? PaymentStatus.Succeeded : PaymentStatus.Failed;
            if (current.Status != expected)
                // e.g. "succeeded" for a payment recorded as Failed: money moved, a human must review it.
                LogContradictoryWebhook(evt.Id, evt.Type, paymentId, current.Status);
        }
        return WebhookResult.Processed;
    }

    /// <summary>Settles Unknown payments and recovers requests that died mid-flight.</summary>
    public async Task<int> ReconcileAsync(int batchSize, CancellationToken ct)
    {
        var staleBefore = Now - options.Value.ReconcileAfter;
        var candidates = await db.Payments.AsNoTracking()
            .Where(p => p.Status == PaymentStatus.Unknown ||
                        ((p.Status == PaymentStatus.Processing || p.Status == PaymentStatus.Pending) && p.UpdatedAt < staleBefore))
            .OrderBy(p => p.UpdatedAt)
            .Select(p => new { p.Id, p.Status })
            .Take(batchSize)
            .ToListAsync(ct);

        var settled = 0;
        foreach (var candidate in candidates)
        {
            var result = candidate.Status == PaymentStatus.Pending
                // Still Pending means the provider was never called (Processing is claimed first).
                ? new ProviderChargeResult(ProviderOutcome.Declined, null, "not_submitted")
                : await LookUpAsync(candidate.Id, ct);

            if (result is not null
                && await ApplyProviderResultAsync(candidate.Id, result, ct)
                && result.Outcome != ProviderOutcome.Unknown)
                settled++;
        }
        return settled;
    }

    private async Task<ProviderChargeResult?> LookUpAsync(Guid paymentId, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(options.Value.ProviderTimeout);
            return await provider.GetStatusAsync(paymentId.ToString("N"), timeout.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException && !ct.IsCancellationRequested)
        {
            LogReconciliationLookupFailed(ex, paymentId);
            return null;
        }
    }

    private Task<Payment?> FindByKeyAsync(string merchantId, string key, CancellationToken ct) =>
        db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.MerchantId == merchantId && p.IdempotencyKey == key, ct);

    private Task<Payment> LoadAsync(Guid id) =>
        db.Payments.AsNoTracking().SingleAsync(p => p.Id == id, CancellationToken.None);

    private static CreatePaymentResult Classify(Payment existing, string requestHash)
    {
        if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
            return new CreatePaymentResult(CreatePaymentResultKind.KeyReusedWithDifferentPayload, null);

        return existing.Status is PaymentStatus.Pending or PaymentStatus.Processing
            ? new CreatePaymentResult(CreatePaymentResultKind.InProgress, existing)
            : new CreatePaymentResult(CreatePaymentResultKind.Replayed, existing);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Provider call failed for payment {PaymentId}")]
    private partial void LogProviderCallFailed(Exception ex, Guid paymentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reconciliation lookup failed for payment {PaymentId}")]
    private partial void LogReconciliationLookupFailed(Exception ex, Guid paymentId);

    [LoggerMessage(Level = LogLevel.Critical,
        Message = "Webhook {EventId} says {EventType} but payment {PaymentId} is {Status}; manual review required")]
    private partial void LogContradictoryWebhook(string eventId, string eventType, Guid paymentId, PaymentStatus status);
}
