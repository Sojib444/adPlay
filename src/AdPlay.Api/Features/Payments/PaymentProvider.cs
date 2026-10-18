namespace AdPlay.Api.Features.Payments;

public enum ProviderOutcome
{
    Approved,
    Declined,
    /// <summary>Timeout, connection reset, 5xx, malformed reply: the charge may or may not have happened.</summary>
    Unknown
}

public sealed record ProviderChargeRequest(
    string IdempotencyKey, string OrderId, decimal Amount, string Currency, string PaymentMethodToken);

public sealed record ProviderChargeResult(ProviderOutcome Outcome, string? ProviderReference, string? Reason)
{
    public static ProviderChargeResult Unknown(string reason) => new(ProviderOutcome.Unknown, null, reason);
}

public interface IPaymentProviderClient
{
    /// <summary>Must forward <see cref="ProviderChargeRequest.IdempotencyKey"/> so retries can never double-charge.</summary>
    Task<ProviderChargeResult> ChargeAsync(ProviderChargeRequest request, CancellationToken ct);

    /// <summary>Looks the charge up by the idempotency key we sent.</summary>
    Task<ProviderChargeResult> GetStatusAsync(string idempotencyKey, CancellationToken ct);
}

/// <summary>Development stand-in: amounts ending in .13 are declined, amounts ending in .99 hang until timeout.</summary>
public sealed class FakePaymentProviderClient : IPaymentProviderClient
{
    public async Task<ProviderChargeResult> ChargeAsync(ProviderChargeRequest request, CancellationToken ct)
    {
        var cents = (int)(request.Amount * 100 % 100);
        if (cents == 99) await Task.Delay(Timeout.Infinite, ct);
        await Task.Delay(50, ct);

        return cents == 13
            ? new ProviderChargeResult(ProviderOutcome.Declined, null, "insufficient_funds")
            : new ProviderChargeResult(ProviderOutcome.Approved, "prov_" + request.IdempotencyKey[..8], null);
    }

    public Task<ProviderChargeResult> GetStatusAsync(string idempotencyKey, CancellationToken ct) =>
        Task.FromResult(new ProviderChargeResult(ProviderOutcome.Approved, "prov_" + idempotencyKey[..8], null));
}
