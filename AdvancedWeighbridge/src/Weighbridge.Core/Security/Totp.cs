using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Weighbridge.Core.Security;

/// <summary>RFC 6238 time-based one-time passwords (compatible with Google Authenticator, Microsoft Authenticator, …).</summary>
public static class Totp
{
    public const int StepSeconds = 30;
    public const int Digits = 6;

    public static string NewSecret() => Base32Encode(RandomNumberGenerator.GetBytes(20));

    public static long Step(DateTimeOffset at) => at.ToUnixTimeSeconds() / StepSeconds;

    public static string Compute(string base32Secret, long step) => Compute(Base32Decode(base32Secret), step);

    public static string Compute(byte[] key, long step, int digits = Digits)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(key, counter, hash);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        var modulo = 1;
        for (var i = 0; i < digits; i++) modulo *= 10;
        return (binary % modulo).ToString(new string('0', digits), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Verifies a code within ±<paramref name="window"/> steps. Codes at or before <paramref name="lastUsedStep"/> are refused (replay).</summary>
    public static bool Verify(string base32Secret, string code, DateTimeOffset now, long? lastUsedStep, out long matchedStep, int window = 1)
    {
        matchedStep = -1;
        if (string.IsNullOrWhiteSpace(code)) return false;
        code = code.Trim().Replace(" ", string.Empty, StringComparison.Ordinal);
        if (code.Length != Digits || !code.All(char.IsAsciiDigit)) return false;
        var key = Base32Decode(base32Secret);
        var current = Step(now);
        for (var s = current - window; s <= current + window; s++)
        {
            if (lastUsedStep is { } last && s <= last) continue;
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Compute(key, s)), Encoding.ASCII.GetBytes(code)))
            {
                matchedStep = s;
                return true;
            }
        }
        return false;
    }

    public static string ProvisioningUri(string issuer, string account, string base32Secret) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}?secret={base32Secret}&issuer={Uri.EscapeDataString(issuer)}&digits={Digits}&period={StepSeconds}";

    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Base32Encode(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder((data.Length + 4) / 5 * 8);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static byte[] Base32Decode(string text)
    {
        var clean = text.Trim().TrimEnd('=').Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        var output = new List<byte>(clean.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var ch in clean)
        {
            var v = Alphabet.IndexOf(ch);
            if (v < 0) throw new FormatException($"Invalid base32 character '{ch}'.");
            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }
        return output.ToArray();
    }
}
