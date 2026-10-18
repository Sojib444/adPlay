using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AdPlay.Api.Middleware;

/// <summary>
/// Removes secrets and unnecessary personal data before anything is logged. JSON is redacted by structure;
/// truncated JSON, form bodies and query strings fall back to pattern matching.
/// </summary>
public static partial class SensitiveDataRedactor
{
    public const string Mask = "***";

    // Matched after lower-casing and removing '_' and '-', so access_token, Access-Token and accessToken all match.
    private static readonly HashSet<string> SecretKeys =
    [
        "password", "passwd", "pwd", "newpassword", "oldpassword", "confirmpassword", "pin", "otp",
        "token", "accesstoken", "refreshtoken", "idtoken", "apikey", "secret", "clientsecret", "authorization",
        "cookie", "setcookie", "cardnumber", "pan", "cvv", "cvc", "cvv2", "expiry", "expirydate",
        "paymentmethodtoken", "accountnumber", "iban", "nid", "nationalid", "passport"
    ];

    // Personal data kept only partially masked (last 3 characters).
    private static readonly HashSet<string> PartialKeys = ["mobile", "phone", "phonenumber", "msisdn", "email"];

    public static string Redact(string? content, string? contentType)
    {
        if (string.IsNullOrEmpty(content)) return content ?? "";

        if (contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            try
            {
                var node = JsonNode.Parse(content);
                if (node is not null)
                {
                    RedactNode(node);
                    return node.ToJsonString();
                }
            }
            catch (JsonException)
            {
                // Truncated or invalid JSON: fall back to pattern matching.
            }
        }

        return RedactText(content);
    }

    /// <summary>Pattern-based redaction for partial JSON, form bodies and query strings.</summary>
    public static string RedactText(string content)
    {
        var result = JsonPairPattern().Replace(content, m =>
            IsSensitive(m.Groups["key"].Value) ? $"{m.Groups["prefix"].Value}\"{Mask}\"" : m.Value);

        result = FormPairPattern().Replace(result, m =>
            IsSensitive(m.Groups["key"].Value) ? $"{m.Groups["key"].Value}={Mask}" : m.Value);

        return MaskCardNumbers(result);
    }

    private static void RedactNode(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj.ToList())
                {
                    var normalized = Normalize(key);
                    if (SecretKeys.Contains(normalized))
                        obj[key] = Mask;
                    else if (PartialKeys.Contains(normalized) && value is JsonValue v && v.TryGetValue<string>(out var s))
                        obj[key] = MaskPartially(s);
                    else if (value is JsonValue sv && sv.TryGetValue<string>(out var text))
                    {
                        var masked = MaskCardNumbers(text);
                        if (masked != text) obj[key] = masked;
                    }
                    else if (value is not null)
                        RedactNode(value);
                }
                break;

            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue sv && sv.TryGetValue<string>(out var text))
                    {
                        var masked = MaskCardNumbers(text);
                        if (masked != text) array[i] = masked;
                    }
                    else if (array[i] is { } item)
                        RedactNode(item);
                }
                break;
        }
    }

    private static bool IsSensitive(string key)
    {
        var n = Normalize(key);
        return SecretKeys.Contains(n) || PartialKeys.Contains(n);
    }

    private static string Normalize(string key) =>
        key.Replace("_", "").Replace("-", "").ToLowerInvariant();

    private static string MaskPartially(string value) =>
        value.Length <= 3 ? Mask : Mask + value[^3..];

    // Any 13-19 digit run (optionally spaced/dashed) that passes Luhn is treated as a card number.
    private static string MaskCardNumbers(string input) =>
        CardPattern().Replace(input, m =>
        {
            var digits = new string(m.Value.Where(char.IsAsciiDigit).ToArray());
            return digits.Length is >= 13 and <= 19 && PassesLuhn(digits) ? Mask + digits[^4..] : m.Value;
        });

    private static bool PassesLuhn(string digits)
    {
        var sum = 0;
        var alt = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var d = digits[i] - '0';
            if (alt) { d *= 2; if (d > 9) d -= 9; }
            sum += d;
            alt = !alt;
        }
        return sum % 10 == 0;
    }

    [GeneratedRegex("""(?<prefix>"(?<key>[A-Za-z0-9_\-]+)"\s*:\s*)("(?:[^"\\]|\\.)*"?|[^,{}\[\]\s"]+)""")]
    private static partial Regex JsonPairPattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9_\-])(?<key>[A-Za-z0-9_\-]+)=(?<value>[^&\s]*)")]
    private static partial Regex FormPairPattern();

    [GeneratedRegex(@"(?<!\d)(?:\d[ \-]?){12,18}\d(?!\d)")]
    private static partial Regex CardPattern();
}
