using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AdPlay.Api.Features.Payments;

[ApiController]
[Route("api/payments/webhooks")]
public sealed partial class PaymentWebhooksController(
    PaymentService payments,
    IOptions<PaymentOptions> options,
    TimeProvider clock,
    ILogger<PaymentWebhooksController> logger) : ControllerBase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Processed, duplicate and ignored events all return 200 so the provider stops retrying.
    /// A 5xx makes it retry, which is safe because processing is idempotent.
    /// </summary>
    /// <response code="401">Invalid or expired signature.</response>
    [HttpPost("{provider}")]
    [RequestSizeLimit(64 * 1024)]
    public async Task<IActionResult> Receive(string provider, CancellationToken ct)
    {
        if (!options.Value.WebhookSecrets.TryGetValue(provider, out var secret))
            return NotFound();

        // Verify the exact raw bytes; re-serialising parsed JSON would change them.
        using var reader = new StreamReader(Request.Body);
        var rawBody = await reader.ReadToEndAsync(ct);

        if (!WebhookSignature.Verify(Request.Headers["X-Provider-Signature"], rawBody, secret,
                clock.GetUtcNow(), options.Value.WebhookTolerance))
        {
            LogInvalidSignature(provider);
            return Unauthorized();
        }

        var evt = TryParse(rawBody);
        if (evt is null || string.IsNullOrWhiteSpace(evt.Id) || string.IsNullOrWhiteSpace(evt.Type))
            return BadRequest();

        var result = await payments.HandleWebhookAsync(provider, evt, ct);
        if (result == WebhookResult.PaymentNotFound)
            LogUnknownPayment(evt.Id, evt.PaymentId);

        return Ok(new { received = true, result = result.ToString() });
    }

    private static ProviderWebhookEvent? TryParse(string rawBody)
    {
        try { return JsonSerializer.Deserialize<ProviderWebhookEvent>(rawBody, Json); }
        catch (JsonException) { return null; }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected webhook with invalid signature from {Provider}")]
    private partial void LogInvalidSignature(string provider);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook {EventId} references unknown payment {PaymentId}")]
    private partial void LogUnknownPayment(string eventId, string? paymentId);
}
