using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Weighbridge.Application.Security;

/// <summary>Encrypts secrets stored in settings (cloud site key, camera passwords, MFA seeds, ticket signing key).</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedText);
}

public static class SecretProtectors
{
    /// <summary>Windows: DPAPI machine scope (bound to this PC). Elsewhere: AES-GCM with a key file readable only by the owner.</summary>
    public static ISecretProtector Create(string keyDirectory) =>
        OperatingSystem.IsWindows() ? new DpapiSecretProtector() : new FileKeySecretProtector(keyDirectory);
}

[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AWMS secrets v1");

    public string Protect(string plaintext) =>
        "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.LocalMachine));

    public string Unprotect(string protectedText)
    {
        if (!protectedText.StartsWith("dpapi:", StringComparison.Ordinal)) throw new CryptographicException("Not a DPAPI-protected value.");
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedText[6..]), Entropy, DataProtectionScope.LocalMachine));
    }
}

public sealed class FileKeySecretProtector : ISecretProtector
{
    private readonly byte[] _key;

    public FileKeySecretProtector(string keyDirectory)
    {
        Directory.CreateDirectory(keyDirectory);
        var path = Path.Combine(keyDirectory, "secrets.key");
        if (!File.Exists(path))
        {
            File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(32));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        _key = File.ReadAllBytes(path);
        if (_key.Length != 32) throw new CryptographicException("The secrets key file is damaged.");
    }

    public string Protect(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var data = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, data, cipher, tag);
        return "aesgcm:" + Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    public string Unprotect(string protectedText)
    {
        if (!protectedText.StartsWith("aesgcm:", StringComparison.Ordinal)) throw new CryptographicException("Not an AES-GCM protected value.");
        var all = Convert.FromBase64String(protectedText[7..]);
        var plain = new byte[all.Length - 28];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(all.AsSpan(0, 12), all.AsSpan(28), all.AsSpan(12, 16), plain);
        return Encoding.UTF8.GetString(plain);
    }
}
