using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text.Json;
using AdPlay.Api.Domain;

namespace AdPlay.Api.Features.Payments;

/// <summary>Card data never reaches this API; the client sends a provider-issued token (keeps us out of PCI scope).</summary>
public sealed record CreatePaymentRequest(
    [property: Required, StringLength(64, MinimumLength = 1)] string OrderId,
    [property: Range(typeof(decimal), "0.01", "10000000")] decimal Amount,
    [property: Required, RegularExpression("^[A-Z]{3}$")] string Currency,
    [property: Required, StringLength(200, MinimumLength = 1)] string PaymentMethodToken);

public sealed record PaymentResponse(
    Guid Id,
    string OrderId,
    decimal Amount,
    string Currency,
    string Status,
    string? ProviderReference,
    string? FailureReason,
    DateTime CreatedAt,
    DateTime UpdatedAt)
{
    public static PaymentResponse From(Payment p) => new(
        p.Id, p.OrderId, p.Amount, p.Currency, p.Status.ToString(),
        p.ProviderReference, p.FailureReason, p.CreatedAt, p.UpdatedAt);
}

public sealed class PaymentOptions
{
    public TimeSpan ProviderTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan WebhookTolerance { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan ReconcileAfter { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan ReconcileInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Provider name → HMAC secret. Load from a secret store, not source control.</summary>
    public Dictionary<string, string> WebhookSecrets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class RequestHasher
{
    private static readonly JsonSerializerOptions Canonical = new(JsonSerializerDefaults.Web);

    /// <summary>Fingerprint of the business payload, so a key reused with a different body can be rejected.</summary>
    public static string Hash(CreatePaymentRequest request)
    {
        var normalized = request with { Amount = decimal.Round(request.Amount, 2), Currency = request.Currency.ToUpperInvariant() };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(normalized, Canonical);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    public static bool IsValidIdempotencyKey(string? key) =>
        !string.IsNullOrWhiteSpace(key) && key.Length <= 64 && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
