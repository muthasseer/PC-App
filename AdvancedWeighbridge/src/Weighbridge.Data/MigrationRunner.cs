using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Weighbridge.Data;

public sealed record Migration(int Version, string Name, string Sql, string Checksum);

public sealed record MigrationResult(int FromVersion, int ToVersion, IReadOnlyList<string> Applied);

/// <summary>
/// Applies the embedded, versioned SQL scripts for the configured engine. Each script runs in its own transaction.
/// Already-applied scripts are checksum-verified, and a database written by a newer version is refused.
/// </summary>
public static partial class MigrationRunner
{
    [GeneratedRegex(@"Migrations\.(\w+)\.(\d{3})_([\w]+)\.sql$")]
    private static partial Regex ResourceName();

    public static IReadOnlyList<Migration> Load(DatabaseProvider provider, Assembly? assembly = null, string? folder = null)
    {
        folder ??= provider == DatabaseProvider.Sqlite ? "Sqlite" : "PostgreSql";
        assembly ??= typeof(MigrationRunner).Assembly;
        var list = new List<Migration>();
        foreach (var name in assembly.GetManifestResourceNames())
        {
            var m = ResourceName().Match(name);
            if (!m.Success || m.Groups[1].Value != folder) continue;
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var sql = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
            var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
            list.Add(new Migration(int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), m.Groups[3].Value, sql, checksum));
        }
        return list.OrderBy(x => x.Version).ToList();
    }

    public static int LatestVersion(DatabaseProvider provider) => Load(provider).Select(m => m.Version).DefaultIfEmpty(0).Max();

    public static async Task<int> CurrentVersionAsync(Database db, CancellationToken ct = default)
    {
        await using var s = await db.OpenAsync(ct).ConfigureAwait(false);
        await EnsureTableAsync(s, ct).ConfigureAwait(false);
        return await s.ExecuteScalarAsync<int?>("SELECT MAX(version) FROM schema_migrations", ct: ct).ConfigureAwait(false) ?? 0;
    }

    /// <param name="beforeApply">Called once before the first pending migration on a database that already has a schema
    /// (the application takes a safety backup here).</param>
    public static async Task<MigrationResult> MigrateAsync(Database db, ILogger? log = null, Func<int, Task>? beforeApply = null, CancellationToken ct = default,
        Assembly? assembly = null, string? folder = null)
    {
        log ??= NullLogger.Instance;
        var migrations = Load(db.Options.Provider, assembly, folder);
        Dictionary<int, string> applied;
        await using (var s = await db.OpenAsync(ct).ConfigureAwait(false))
        {
            await EnsureTableAsync(s, ct).ConfigureAwait(false);
            applied = (await s.QueryAsync<(long Version, string Checksum)>("SELECT version, checksum FROM schema_migrations", ct: ct).ConfigureAwait(false))
                .ToDictionary(r => (int)r.Version, r => r.Checksum);
        }

        var known = migrations.ToDictionary(m => m.Version);
        var newest = applied.Keys.DefaultIfEmpty(0).Max();
        if (newest > migrations.Select(m => m.Version).DefaultIfEmpty(0).Max())
        {
            throw new InvalidOperationException($"The database schema (version {newest}) is newer than this application supports. Install the matching or a newer version.");
        }
        foreach (var (version, checksum) in applied)
        {
            if (known.TryGetValue(version, out var m) && !string.Equals(m.Checksum, checksum, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Migration {version:000} was modified after it was applied (checksum mismatch).");
            }
        }

        var pending = migrations.Where(m => !applied.ContainsKey(m.Version)).ToList();
        var names = new List<string>();
        if (pending.Count > 0 && applied.Count > 0 && beforeApply is not null) await beforeApply(newest).ConfigureAwait(false);

        foreach (var m in pending)
        {
            log.LogInformation("Applying database migration {Version:000} {Name}", m.Version, m.Name);
            await using var s = await db.BeginAsync(ct).ConfigureAwait(false);
            await s.ExecuteAsync(m.Sql, ct: ct).ConfigureAwait(false);
            await s.ExecuteAsync("INSERT INTO schema_migrations (version, name, applied_utc, checksum) VALUES (@Version, @Name, @At, @Checksum)",
                new { m.Version, m.Name, At = Iso.ToText(DateTimeOffset.UtcNow), m.Checksum }, ct).ConfigureAwait(false);
            await s.CommitAsync(ct).ConfigureAwait(false);
            names.Add($"{m.Version:000}_{m.Name}");
        }
        return new MigrationResult(newest, pending.Count > 0 ? pending[^1].Version : newest, names);
    }

    private static Task EnsureTableAsync(DbSession s, CancellationToken ct) => s.ExecuteAsync(
        "CREATE TABLE IF NOT EXISTS schema_migrations (version INTEGER PRIMARY KEY, name TEXT NOT NULL, applied_utc TEXT NOT NULL, checksum TEXT NOT NULL)", ct: ct);
}
