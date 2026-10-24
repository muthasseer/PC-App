using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Weighbridge.Reporting.Tickets;

/// <summary>
/// Tamper-evident QR payload printed on tickets: ticket number, transaction id, net grams and an HMAC signature
/// with the installation's secret key. A forged or altered paper ticket fails verification (Tools → Verify ticket,
/// or the cloud verification page when a URL is configured).
/// </summary>
public static class TicketVerification
{
    public sealed record Parsed(string TicketNo, Guid Uid, long NetGrams, string Signature);

    public static string Sign(byte[] key, string ticketNo, Guid uid, long netGrams)
    {
        var canonical = string.Join('|', "AWMS1", ticketNo, uid.ToString("N"), netGrams.ToString(CultureInfo.InvariantCulture));
        var mac = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(canonical));
        return Core.Security.Totp.Base32Encode(mac.AsSpan(0, 10));
    }

    /// <summary>Builds the QR content; with a base URL it is a link that phones open directly.</summary>
    public static string CreatePayload(byte[] key, string ticketNo, Guid uid, long netGrams, string? verifyBaseUrl = null)
    {
        var sig = Sign(key, ticketNo, uid, netGrams);
        if (!string.IsNullOrWhiteSpace(verifyBaseUrl))
        {
            return $"{verifyBaseUrl.TrimEnd('/')}/verify?t={Uri.EscapeDataString(ticketNo)}&u={uid:N}&n={netGrams.ToString(CultureInfo.InvariantCulture)}&s={sig}";
        }
        return $"AWMS1:{ticketNo}:{uid:N}:{netGrams.ToString(CultureInfo.InvariantCulture)}:{sig}";
    }

    public static Parsed? Parse(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;
        payload = payload.Trim();
        if (payload.StartsWith("AWMS1:", StringComparison.Ordinal))
        {
            var parts = payload.Split(':');
            if (parts.Length != 5) return null;
            return Guid.TryParseExact(parts[2], "N", out var uid) && long.TryParse(parts[3], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)
                ? new Parsed(parts[1], uid, n, parts[4])
                : null;
        }
        if (Uri.TryCreate(payload, UriKind.Absolute, out var uri))
        {
            var q = System.Web.HttpUtility.ParseQueryString(uri.Query);
            return q["t"] is { } t && Guid.TryParseExact(q["u"], "N", out var uid2) && long.TryParse(q["n"], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n2) && q["s"] is { } s
                ? new Parsed(t, uid2, n2, s)
                : null;
        }
        return null;
    }

    public static bool Verify(byte[] key, Parsed p) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(key, p.TicketNo, p.Uid, p.NetGrams)), Encoding.ASCII.GetBytes(p.Signature));
}
