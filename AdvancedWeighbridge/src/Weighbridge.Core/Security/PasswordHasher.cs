using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Weighbridge.Core.Security;

/// <summary>
/// PBKDF2-HMAC-SHA256 password hashing (OWASP 2023 recommendation: 600 000 iterations).
/// Format: <c>PBKDF2-SHA256$iterations$saltBase64$hashBase64</c>.
/// </summary>
public static class PasswordHasher
{
    public const int DefaultIterations = 600_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const string Prefix = "PBKDF2-SHA256";

    public static string Hash(string password, int iterations = DefaultIterations)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (iterations < 10_000) throw new ArgumentOutOfRangeException(nameof(iterations));
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        return string.Join('$', Prefix, iterations.ToString(CultureInfo.InvariantCulture), Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    public static bool Verify(string password, string? encoded)
    {
        if (password is null || string.IsNullOrEmpty(encoded)) return false;
        var parts = encoded.Split('$');
        if (parts.Length != 4 || parts[0] != Prefix) return false;
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations) || iterations < 10_000) return false;
        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }
        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public static bool NeedsRehash(string encoded, int iterations = DefaultIterations)
    {
        var parts = encoded.Split('$');
        return parts.Length != 4 || parts[0] != Prefix || !int.TryParse(parts[1], CultureInfo.InvariantCulture, out var n) || n < iterations;
    }
}

public sealed record PasswordPolicy
{
    public int MinimumLength { get; init; } = 8;
    public bool RequireLetter { get; init; } = true;
    public bool RequireDigit { get; init; } = true;
    public bool RequireSymbolOrUpper { get; init; }

    public IReadOnlyList<string> Check(string password, string username)
    {
        var problems = new List<string>();
        if (string.IsNullOrEmpty(password) || password.Length < MinimumLength) problems.Add($"At least {MinimumLength} characters are required.");
        if (RequireLetter && !password.Any(char.IsLetter)) problems.Add("A letter is required.");
        if (RequireDigit && !password.Any(char.IsDigit)) problems.Add("A digit is required.");
        if (RequireSymbolOrUpper && !password.Any(c => char.IsUpper(c) || !char.IsLetterOrDigit(c))) problems.Add("An upper-case letter or a symbol is required.");
        if (!string.IsNullOrEmpty(username) && password.Contains(username, StringComparison.OrdinalIgnoreCase)) problems.Add("The password must not contain the user name.");
        return problems;
    }
}
