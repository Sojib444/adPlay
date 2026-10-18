using AdPlay.Api.Domain;
using Microsoft.AspNetCore.Mvc;

namespace AdPlay.Api.Features.Payments;

[ApiController]
[Route("api/payments")]
public sealed class PaymentsController(PaymentService payments) : ControllerBase
{
    /// <summary>A replay returns the original status and body, plus <c>Idempotent-Replayed: true</c>.</summary>
    /// <response code="201">Payment succeeded.</response>
    /// <response code="202">Outcome pending; poll GET or wait for the webhook.</response>
    /// <response code="402">Declined by the provider.</response>
    /// <response code="409">Same key still in progress, or the order already has a live payment.</response>
    /// <response code="422">Idempotency-Key reused with a different payload.</response>
    [HttpPost]
    public async Task<IActionResult> Create(
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        [FromBody] CreatePaymentRequest request,
        CancellationToken ct)
    {
        var merchantId = ResolveMerchantId();
        if (merchantId is null)
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Merchant not identified");

        if (!RequestHasher.IsValidIdempotencyKey(idempotencyKey))
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid Idempotency-Key",
                detail: "Send a unique Idempotency-Key header (1-64 chars: letters, digits, '-', '_') per payment attempt.");

        var result = await payments.CreateAsync(merchantId, idempotencyKey!, request, ct);

        switch (result.Kind)
        {
            case CreatePaymentResultKind.KeyReusedWithDifferentPayload:
                return Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Idempotency-Key reused",
                    detail: "This Idempotency-Key was already used with a different request body.");

            case CreatePaymentResultKind.InProgress:
                Response.Headers.RetryAfter = "2";
                return Problem(statusCode: StatusCodes.Status409Conflict, title: "Request in progress",
                    detail: "A request with this Idempotency-Key is still being processed. Retry shortly.");

            case CreatePaymentResultKind.DuplicateOrder:
                var conflict = ProblemDetailsFactory.CreateProblemDetails(HttpContext, StatusCodes.Status409Conflict,
                    "Duplicate payment", detail: "This order already has a payment that is pending or succeeded.");
                conflict.Extensions["paymentId"] = result.Payment?.Id;
                return new ObjectResult(conflict) { StatusCode = StatusCodes.Status409Conflict };

            case CreatePaymentResultKind.Replayed:
                Response.Headers["Idempotent-Replayed"] = "true";
                return FromPayment(result.Payment!);

            default:
                return FromPayment(result.Payment!);
        }
    }

    [HttpGet("{id:guid}", Name = "GetPayment")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var merchantId = ResolveMerchantId();
        if (merchantId is null) return Unauthorized();

        var payment = await payments.GetAsync(merchantId, id, ct);
        return payment is null ? NotFound() : Ok(PaymentResponse.From(payment));
    }

    private ObjectResult FromPayment(Payment p)
    {
        var body = PaymentResponse.From(p);
        return p.Status switch
        {
            PaymentStatus.Succeeded => CreatedAtRoute("GetPayment", new { id = p.Id }, body),
            PaymentStatus.Failed => new ObjectResult(body) { StatusCode = StatusCodes.Status402PaymentRequired },
            _ => AcceptedAtRoute("GetPayment", new { id = p.Id }, body)
        };
    }

    // Production reads only the authenticated principal; the header fallback lets the demo run without auth.
    private string? ResolveMerchantId() =>
        User.FindFirst("merchant_id")?.Value
        ?? (Request.Headers.TryGetValue("X-Merchant-Id", out var h) && !string.IsNullOrWhiteSpace(h) ? h.ToString() : null);
}
