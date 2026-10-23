using System.Security.Cryptography;
using System.Text;

namespace Weighbridge.Cloud.Services;

/// <summary>AES-256-GCM encryption for secrets stored in the database (MFA secrets).</summary>
public sealed class SecretBox(byte[] key)
{
    private const string Prefix = "v1:";

    public string Protect(string plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var data = Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, data, cipher, tag);
        return Prefix + Convert.ToBase64String([.. nonce, .. cipher, .. tag]);
    }

    public string Unprotect(string text)
    {
        if (!text.StartsWith(Prefix, StringComparison.Ordinal)) throw new CryptographicException("Unknown secret format.");
        var all = Convert.FromBase64String(text[Prefix.Length..]);
        if (all.Length < 28) throw new CryptographicException("The protected secret is truncated.");
        var plain = new byte[all.Length - 28];
        using (var aes = new AesGcm(key, 16)) aes.Decrypt(all.AsSpan(0, 12), all.AsSpan(12, plain.Length), all.AsSpan(all.Length - 16), plain);
        return Encoding.UTF8.GetString(plain);
    }
}
