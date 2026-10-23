using System.Text;
using Weighbridge.Core.Security;

namespace Weighbridge.Core.Tests;

public class SecurityTests
{
    [Fact]
    public void Password_hash_round_trip_and_salting()
    {
        var a = PasswordHasher.Hash("Weigh#2026", iterations: 20_000);
        var b = PasswordHasher.Hash("Weigh#2026", iterations: 20_000);
        Assert.NotEqual(a, b);
        Assert.True(PasswordHasher.Verify("Weigh#2026", a));
        Assert.False(PasswordHasher.Verify("weigh#2026", a));
        Assert.False(PasswordHasher.Verify("Weigh#2026", "garbage"));
        Assert.False(PasswordHasher.Verify("Weigh#2026", null));
        Assert.True(PasswordHasher.NeedsRehash(a));
        Assert.StartsWith("PBKDF2-SHA256$20000$", a);
    }

    [Fact]
    public void Password_policy()
    {
        var policy = new PasswordPolicy();
        Assert.Empty(policy.Check("scale2026x", "operator"));
        Assert.NotEmpty(policy.Check("short1", "op"));
        Assert.NotEmpty(policy.Check("onlyletters", "op"));
        Assert.NotEmpty(policy.Check("operator123", "operator"));
    }

    // RFC 6238 Appendix B test vectors (SHA-1, 8 digits, key "12345678901234567890").
    [Theory]
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1111111111L, "14050471")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(2000000000L, "69279037")]
    public void Totp_rfc6238_vectors(long unixTime, string expected)
    {
        var key = Encoding.ASCII.GetBytes("12345678901234567890");
        Assert.Equal(expected, Totp.Compute(key, unixTime / 30, digits: 8));
    }

    [Fact]
    public void Totp_verify_window_and_replay_protection()
    {
        var secret = Totp.NewSecret();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var code = Totp.Compute(secret, Totp.Step(now));
        Assert.True(Totp.Verify(secret, code, now.AddSeconds(25), null, out var step));
        Assert.False(Totp.Verify(secret, code, now, step, out _)); // replay
        Assert.False(Totp.Verify(secret, code, now.AddMinutes(5), null, out _));
        Assert.False(Totp.Verify(secret, "12ab56", now, null, out _));
        Assert.StartsWith("otpauth://totp/AWMS:admin?secret=", Totp.ProvisioningUri("AWMS", "admin", secret));
    }

    [Fact]
    public void Base32_round_trip()
    {
        var data = Enumerable.Range(0, 41).Select(i => (byte)(i * 7)).ToArray();
        Assert.Equal(data, Totp.Base32Decode(Totp.Base32Encode(data)));
        Assert.Equal("MZXW6YTBOI", Totp.Base32Encode(Encoding.ASCII.GetBytes("foobar")));
    }

    [Fact]
    public void Role_defaults_are_consistent()
    {
        Assert.Equal(Permissions.All.Count, Roles.Defaults[Roles.Administrator].Count);
        foreach (var (_, perms) in Roles.Defaults) Assert.All(perms, p => Assert.Contains(p, Permissions.All));
        Assert.DoesNotContain(Permissions.UsersManage, Roles.Defaults[Roles.Manager]);
        Assert.DoesNotContain(Permissions.WeighingCapture, Roles.Defaults[Roles.Auditor]);
        var ctx = new UserContext(1, "op", "Op", Roles.Operator, Roles.Defaults[Roles.Operator].ToHashSet());
        Assert.Throws<PermissionDeniedException>(() => ctx.Demand(Permissions.TransactionCancel));
        ctx.Demand(Permissions.WeighingCapture);
    }
}
