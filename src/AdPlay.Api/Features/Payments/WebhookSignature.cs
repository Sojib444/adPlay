using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AdPlay.Api.Features.Payments;

/// <summary>
/// Verifies <c>t=&lt;unix&gt;,v1=&lt;hex&gt;[,v1=...]</c> where v1 = HMAC-SHA256(secret, "{t}.{rawBody}").
/// The signed timestamp blocks replays; several v1 values allow secret rotation.
/// </summary>
public static class WebhookSignature
{
    public static string Compute(string secret, long timestamp, string rawBody)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{rawBody}"));
        return Convert.ToHexStringLower(mac);
    }

    public static bool Verify(string? header, string rawBody, string secret, DateTimeOffset now, TimeSpan tolerance)
    {
        if (string.IsNullOrWhiteSpace(header) || string.IsNullOrEmpty(secret)) return false;

        long? timestamp = null;
        var signatures = new List<string>();
        foreach (var part in header.Split(',', StringSplitOptions.TrimEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            if (kv[0] == "t" && long.TryParse(kv[1], NumberStyles.None, CultureInfo.InvariantCulture, out var t)) timestamp = t;
            else if (kv[0] == "v1") signatures.Add(kv[1]);
        }

        if (timestamp is null || signatures.Count == 0) return false;
        if ((now - DateTimeOffset.FromUnixTimeSeconds(timestamp.Value)).Duration() > tolerance) return false;

        var expected = Encoding.ASCII.GetBytes(Compute(secret, timestamp.Value, rawBody));
        // Constant-time comparison so timing doesn't leak how many characters matched.
        return signatures.Any(s => CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(s.ToLowerInvariant())));
    }
}
