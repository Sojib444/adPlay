using AdPlay.Api.Features.Payments;
using AdPlay.Api.Middleware;

namespace AdPlay.Tests;

public class SensitiveDataRedactorTests
{
    [Fact]
    public void Redacts_secrets_in_nested_json_and_partially_masks_personal_data()
    {
        const string json = """
            {"user":{"email":"someone@example.com","Password":"hunter2","profile":{"mobile":"8801712345678"}},
             "access_token":"abc.def.ghi","items":[{"cardNumber":"4111 1111 1111 1111"}],"note":"card 4111111111111111"}
            """;

        var result = SensitiveDataRedactor.Redact(json, "application/json");

        Assert.DoesNotContain("hunter2", result);
        Assert.DoesNotContain("abc.def.ghi", result);
        Assert.DoesNotContain("4111111111111111", result);
        Assert.DoesNotContain("4111 1111 1111 1111", result);
        Assert.DoesNotContain("someone@example.com", result);
        Assert.Contains("***678", result);   // mobile keeps last 3 chars
        Assert.Contains("***1111", result);  // PAN in free text keeps last 4
    }

    [Fact]
    public void Falls_back_to_pattern_redaction_for_truncated_json()
    {
        const string truncated = """{"username":"bob","password":"hunter2","token":"tok_12""";

        var result = SensitiveDataRedactor.Redact(truncated, "application/json");

        Assert.Contains("\"username\":\"bob\"", result);
        Assert.DoesNotContain("hunter2", result);
        Assert.DoesNotContain("tok_12", result);
    }

    [Fact]
    public void Redacts_form_bodies_and_query_strings()
    {
        var result = SensitiveDataRedactor.RedactText("?user=bob&password=hunter2&access_token=xyz&page=2");

        Assert.Equal("?user=bob&password=***&access_token=***&page=2", result);
    }

    [Fact]
    public void Leaves_non_card_digit_runs_alone()
    {
        Assert.Equal("order 1234567890123", SensitiveDataRedactor.RedactText("order 1234567890123"));
    }
}

public class WebhookSignatureTests
{
    private const string Secret = "whsec_test";
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private static string Header(string body, DateTimeOffset at, string secret = Secret)
    {
        var t = at.ToUnixTimeSeconds();
        return $"t={t},v1={WebhookSignature.Compute(secret, t, body)}";
    }

    [Fact]
    public void Accepts_valid_signature()
    {
        Assert.True(WebhookSignature.Verify(Header("{}", Now), "{}", Secret, Now, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Rejects_tampered_body_wrong_secret_and_stale_timestamp()
    {
        var tolerance = TimeSpan.FromMinutes(5);
        Assert.False(WebhookSignature.Verify(Header("{\"a\":1}", Now), "{\"a\":2}", Secret, Now, tolerance));
        Assert.False(WebhookSignature.Verify(Header("{}", Now, "other"), "{}", Secret, Now, tolerance));
        Assert.False(WebhookSignature.Verify(Header("{}", Now.AddMinutes(-10)), "{}", Secret, Now, tolerance));
        Assert.False(WebhookSignature.Verify(null, "{}", Secret, Now, tolerance));
        Assert.False(WebhookSignature.Verify("garbage", "{}", Secret, Now, tolerance));
    }

    [Fact]
    public void Accepts_any_matching_v1_during_secret_rotation()
    {
        var t = Now.ToUnixTimeSeconds();
        var header = $"t={t},v1={WebhookSignature.Compute("old", t, "{}")},v1={WebhookSignature.Compute(Secret, t, "{}")}";
        Assert.True(WebhookSignature.Verify(header, "{}", Secret, Now, TimeSpan.FromMinutes(5)));
    }
}
