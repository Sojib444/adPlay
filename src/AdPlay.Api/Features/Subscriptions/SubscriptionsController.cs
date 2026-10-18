using Microsoft.AspNetCore.Mvc;

namespace AdPlay.Api.Features.Subscriptions;

public sealed record SubscribeRequest(string Mobile);

[ApiController]
[Route("api/subscriptions")]
public sealed class SubscriptionsController(SubscriptionService subscriptions) : ControllerBase
{
    /// <response code="200">This call subscribed the user.</response>
    /// <response code="409">Already subscribed; nothing changed, so retrying is safe.</response>
    /// <response code="404">Unknown mobile number.</response>
    [HttpPost]
    public async Task<IActionResult> Subscribe([FromBody] SubscribeRequest request, CancellationToken ct)
    {
        var mobile = request.Mobile?.Trim();
        if (!SubscriptionService.IsValidMobile(mobile))
            return ValidationProblem(detail: "mobile must be 10-15 digits, optionally prefixed with '+'.");

        return await subscriptions.SubscribeAsync(mobile!, ct) switch
        {
            SubscribeOutcome.Subscribed => Ok(new { mobile, status = "Subscribed" }),
            SubscribeOutcome.AlreadySubscribed => Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Already subscribed",
                detail: "The user already has an active subscription.",
                type: "https://errors.adplay.example/already-subscribed"),
            _ => Problem(statusCode: StatusCodes.Status404NotFound, title: "User not found")
        };
    }
}
