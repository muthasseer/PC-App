using System.Security.Cryptography;
using System.Text.Json;
using Weighbridge.Core.Security;
using Weighbridge.Data;

namespace Weighbridge.Cloud.Services;

/// <summary>Server configuration (section "Cloud"; environment variables use the form <c>Cloud__JwtSigningKey</c>).</summary>
public sealed class CloudOptions
{
    public const string Section = "Cloud";

    /// <summary>Folder for the SQLite database and the generated key file. Relative paths are resolved against the content root.</summary>
    public string DataDirectory { get; set; } = "data";

    /// <summary><c>Sqlite</c> (small installations) or <c>PostgreSql</c> (recommended for production).</summary>
    public string DatabaseProvider { get; set; } = "Sqlite";

    /// <summary>PostgreSQL connection string, or an optional SQLite file path. Empty for SQLite = <c>{DataDirectory}/awms-cloud.db</c>.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>HMAC key for access tokens (at least 32 characters). Empty = generated once and kept in the key file.</summary>
    public string? JwtSigningKey { get; set; }

    public string JwtIssuer { get; set; } = "awms-cloud";
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 14;

    /// <summary>Base64 AES-256 key that encrypts stored MFA secrets. Empty = generated once and kept in the key file.</summary>
    public string? DataProtectionKey { get; set; }

    /// <summary>First administrator, created only when the server has no users yet.</summary>
    public string? BootstrapAdminUsername { get; set; }
    public string? BootstrapAdminPassword { get; set; }

    /// <summary>A site without a heartbeat for this long is reported offline (a notification is raised once).</summary>
    public int SiteOfflineAfterMinutes { get; set; } = 5;

    /// <summary>Live weight older than this is flagged as not live in the dashboard and the mobile app.</summary>
    public int LiveDataStaleSeconds { get; set; } = 90;

    /// <summary>Time zone used for daily report grouping and exported dates (IANA or Windows id).</summary>
    public string TimeZone { get; set; } = "UTC";

    /// <summary>Accept X-Forwarded-For/Proto from the reverse proxy (enable only when the server is reachable through the proxy alone).</summary>
    public bool TrustForwardedHeaders { get; set; }

    public int MaxFailedLogins { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
    public int LoginRateLimitPerMinute { get; set; } = 20;

    /// <summary>Refuses administrator sign-in until the administrator has enabled MFA (the MFA set-up itself stays reachable).</summary>
    public bool RequireMfaForAdministrators { get; set; }

    public PasswordPolicy PasswordPolicy { get; set; } = new() { MinimumLength = 10, RequireSymbolOrUpper = true };

    public DatabaseProvider Provider => DatabaseProvider.Equals("PostgreSql", StringComparison.OrdinalIgnoreCase) || DatabaseProvider.Equals("Postgres", StringComparison.OrdinalIgnoreCase)
        ? Data.DatabaseProvider.PostgreSql
        : Data.DatabaseProvider.Sqlite;

    public string ResolveDataDirectory(string contentRoot) => Path.GetFullPath(Path.IsPathRooted(DataDirectory) ? DataDirectory : Path.Combine(contentRoot, DataDirectory));

    public DatabaseOptions DatabaseOptions(string contentRoot)
    {
        if (Provider == Data.DatabaseProvider.PostgreSql) return new DatabaseOptions(Data.DatabaseProvider.PostgreSql, ConnectionString!);
        var path = string.IsNullOrWhiteSpace(ConnectionString) ? Path.Combine(ResolveDataDirectory(contentRoot), "awms-cloud.db") : ConnectionString;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        return Data.DatabaseOptions.SqliteFile(path);
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!DatabaseProvider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase) && Provider != Data.DatabaseProvider.PostgreSql)
        {
            errors.Add($"Cloud:DatabaseProvider must be Sqlite or PostgreSql (was '{DatabaseProvider}').");
        }
        if (Provider == Data.DatabaseProvider.PostgreSql && string.IsNullOrWhiteSpace(ConnectionString)) errors.Add("Cloud:ConnectionString is required for PostgreSQL.");
        if (!string.IsNullOrEmpty(JwtSigningKey) && JwtSigningKey.Length < 32) errors.Add("Cloud:JwtSigningKey must be at least 32 characters.");
        if (!string.IsNullOrEmpty(DataProtectionKey) && TryKey(DataProtectionKey) is null) errors.Add("Cloud:DataProtectionKey must be 32 bytes, Base64 encoded.");
        if (AccessTokenMinutes is < 1 or > 120) errors.Add("Cloud:AccessTokenMinutes must be between 1 and 120.");
        if (RefreshTokenDays is < 1 or > 90) errors.Add("Cloud:RefreshTokenDays must be between 1 and 90.");
        if (SiteOfflineAfterMinutes < 1) errors.Add("Cloud:SiteOfflineAfterMinutes must be at least 1.");
        if (LiveDataStaleSeconds < 5) errors.Add("Cloud:LiveDataStaleSeconds must be at least 5.");
        if (MaxFailedLogins < 1 || LockoutMinutes < 1) errors.Add("Cloud:MaxFailedLogins and Cloud:LockoutMinutes must be positive.");
        if (LoginRateLimitPerMinute < 1) errors.Add("Cloud:LoginRateLimitPerMinute must be positive.");
        if (PasswordPolicy.MinimumLength < 8) errors.Add("Cloud:PasswordPolicy:MinimumLength must be at least 8.");
        if (ResolveTimeZone(TimeZone) is null) errors.Add($"Cloud:TimeZone '{TimeZone}' is not a known time zone.");
        return errors;
    }

    public TimeZoneInfo TimeZoneInfo => ResolveTimeZone(TimeZone) ?? System.TimeZoneInfo.Utc;

    private static TimeZoneInfo? ResolveTimeZone(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Equals("UTC", StringComparison.OrdinalIgnoreCase)) return System.TimeZoneInfo.Utc;
        try { return System.TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { return null; }
    }

    internal static byte[]? TryKey(string base64)
    {
        try
        {
            var bytes = Convert.FromBase64String(base64);
            return bytes.Length == 32 ? bytes : null;
        }
        catch (FormatException) { return null; }
    }
}

/// <summary>The secrets actually used by the server: configured values, or values generated once and kept in a key file.</summary>
public sealed record CloudKeys(byte[] JwtKey, byte[] DataKey, string Source)
{
    private sealed record KeyFile(string JwtSigningKey, string DataProtectionKey, DateTimeOffset CreatedUtc);

    public static CloudKeys Resolve(CloudOptions o, string dataDirectory)
    {
        var jwt = string.IsNullOrEmpty(o.JwtSigningKey) ? null : System.Text.Encoding.UTF8.GetBytes(o.JwtSigningKey);
        var data = string.IsNullOrEmpty(o.DataProtectionKey) ? null : CloudOptions.TryKey(o.DataProtectionKey);
        if (jwt is not null && data is not null) return new CloudKeys(jwt, data, "configuration");

        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, "cloud-keys.json");
        KeyFile file;
        if (File.Exists(path))
        {
            file = JsonSerializer.Deserialize<KeyFile>(File.ReadAllText(path)) ?? throw new InvalidOperationException($"The key file {path} is empty or invalid.");
        }
        else
        {
            file = new KeyFile(Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), DateTimeOffset.UtcNow);
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(path, options)) JsonSerializer.Serialize(stream, file);
        }
        return new CloudKeys(jwt ?? System.Text.Encoding.UTF8.GetBytes(file.JwtSigningKey), data ?? CloudOptions.TryKey(file.DataProtectionKey)
            ?? throw new InvalidOperationException($"The key file {path} holds an invalid data key."), $"key file {path}");
    }
}
