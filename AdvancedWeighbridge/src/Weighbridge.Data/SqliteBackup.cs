using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Weighbridge.Data;

public sealed record BackupManifest(
    string Format,
    int FormatVersion,
    string Application,
    string ApplicationVersion,
    DateTimeOffset CreatedUtc,
    string Machine,
    int SchemaVersion,
    long Transactions,
    long AuditEvents,
    string DatabaseSha256,
    int EvidenceFiles);

public sealed record BackupInfo(string Path, long SizeBytes, string Sha256, BackupManifest Manifest);

/// <summary>
/// Backups of the local SQLite database: a consistent online copy (SQLite backup API, safe while the application
/// is running), integrity-checked, packed with a manifest and optionally the camera evidence into a
/// <c>.awmsbak</c> ZIP, plus a <c>.sha256</c> file. Restores keep the replaced database for rollback.
/// </summary>
public static class SqliteBackup
{
    public const string Extension = ".awmsbak";
    private const string DbEntry = "database/weighbridge.db";
    private const string ManifestEntry = "manifest.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static async Task<BackupInfo> CreateAsync(Database db, string targetDirectory, string? evidenceDirectory, string appVersion, DateTimeOffset now, CancellationToken ct = default)
    {
        if (db.Options.Provider != DatabaseProvider.Sqlite) throw new NotSupportedException("In-application backup is for the local SQLite database. Use pg_dump for PostgreSQL.");
        Directory.CreateDirectory(targetDirectory);
        var stamp = now.ToUniversalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var tempDb = Path.Combine(Path.GetTempPath(), $"awms-backup-{stamp}-{Guid.NewGuid():N}.db");
        var target = Path.Combine(targetDirectory, $"AWMS-backup-{stamp}{Extension}");
        try
        {
            await using (var source = (SqliteConnection)await db.OpenConnectionAsync(ct).ConfigureAwait(false))
            await using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = tempDb, Pooling = false }.ToString()))
            {
                await destination.OpenAsync(ct).ConfigureAwait(false);
                source.BackupDatabase(destination);
            }

            var (schema, transactions, audit) = await InspectAsync(tempDb, ct).ConfigureAwait(false);
            var dbHash = await HashFileAsync(tempDb, ct).ConfigureAwait(false);
            var evidenceFiles = evidenceDirectory is not null && Directory.Exists(evidenceDirectory)
                ? Directory.EnumerateFiles(evidenceDirectory, "*", SearchOption.AllDirectories).ToList()
                : [];
            var manifest = new BackupManifest("awms-backup", 1, "Advanced Weighbridge Management System", appVersion, now, Environment.MachineName,
                schema, transactions, audit, dbHash, evidenceFiles.Count);

            await using (var zipStream = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
            using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(tempDb, DbEntry, CompressionLevel.Optimal);
                var entry = zip.CreateEntry(ManifestEntry);
                await using (var w = entry.Open()) await JsonSerializer.SerializeAsync(w, manifest, Json, ct).ConfigureAwait(false);
                foreach (var file in evidenceFiles)
                {
                    var relative = Path.GetRelativePath(evidenceDirectory!, file).Replace('\\', '/');
                    zip.CreateEntryFromFile(file, "evidence/" + relative, CompressionLevel.NoCompression);
                }
            }

            var zipHash = await HashFileAsync(target, ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(target + ".sha256", $"{zipHash}  {Path.GetFileName(target)}\n", ct).ConfigureAwait(false);
            return new BackupInfo(target, new FileInfo(target).Length, zipHash, manifest);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(tempDb);
        }
    }

    /// <summary>Checks the archive hash (if the .sha256 file exists), the manifest and the database integrity.</summary>
    public static async Task<BackupInfo> VerifyAsync(string backupPath, CancellationToken ct = default)
    {
        var zipHash = await HashFileAsync(backupPath, ct).ConfigureAwait(false);
        var sidecar = backupPath + ".sha256";
        if (File.Exists(sidecar))
        {
            var expected = (await File.ReadAllTextAsync(sidecar, ct).ConfigureAwait(false)).Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            if (!string.Equals(expected, zipHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The backup file does not match its SHA-256 checksum (corrupted or modified).");
        }

        var temp = Path.Combine(Path.GetTempPath(), $"awms-verify-{Guid.NewGuid():N}.db");
        try
        {
            BackupManifest manifest;
            using (var zip = ZipFile.OpenRead(backupPath))
            {
                var m = zip.GetEntry(ManifestEntry) ?? throw new InvalidDataException("The backup has no manifest.");
                await using (var r = m.Open())
                {
                    manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(r, Json, ct).ConfigureAwait(false) ?? throw new InvalidDataException("Invalid manifest.");
                }
                if (manifest.Format != "awms-backup") throw new InvalidDataException("Not an AWMS backup.");
                var dbEntry = zip.GetEntry(DbEntry) ?? throw new InvalidDataException("The backup has no database.");
                dbEntry.ExtractToFile(temp);
            }
            var dbHash = await HashFileAsync(temp, ct).ConfigureAwait(false);
            if (!string.Equals(dbHash, manifest.DatabaseSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The database inside the backup does not match the manifest checksum.");
            await InspectAsync(temp, ct).ConfigureAwait(false);
            return new BackupInfo(backupPath, new FileInfo(backupPath).Length, zipHash, manifest);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(temp);
        }
    }

    /// <summary>
    /// Replaces the database with the backup. The application must have released its connections (it restarts after
    /// a restore). The current database files are moved to a <c>pre-restore-*</c> folder, never deleted.
    /// </summary>
    public static async Task<string> RestoreAsync(string backupPath, string databasePath, string? evidenceDirectory, DateTimeOffset now, CancellationToken ct = default)
    {
        await VerifyAsync(backupPath, ct).ConfigureAwait(false);
        SqliteConnection.ClearAllPools();

        var folder = Path.GetDirectoryName(Path.GetFullPath(databasePath))!;
        var keep = Path.Combine(folder, "pre-restore-" + now.ToUniversalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(keep);
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var f = databasePath + suffix;
            if (File.Exists(f)) File.Move(f, Path.Combine(keep, Path.GetFileName(f)));
        }

        using var zip = ZipFile.OpenRead(backupPath);
        zip.GetEntry(DbEntry)!.ExtractToFile(databasePath, overwrite: false);
        if (evidenceDirectory is not null)
        {
            var root = Path.GetFullPath(evidenceDirectory);
            foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("evidence/", StringComparison.Ordinal) && e.Name.Length > 0))
            {
                var target = Path.GetFullPath(Path.Combine(root, entry.FullName["evidence/".Length..]));
                if (!target.StartsWith(root, StringComparison.Ordinal)) throw new InvalidDataException("Unsafe path in backup.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (!File.Exists(target)) entry.ExtractToFile(target);
            }
        }
        return keep;
    }

    /// <summary>Deletes the oldest backup archives beyond <paramref name="keep"/>. Only backup files are touched.</summary>
    public static IReadOnlyList<string> Prune(string directory, int keep)
    {
        if (keep < 1 || !Directory.Exists(directory)) return [];
        var files = new DirectoryInfo(directory).GetFiles("AWMS-backup-*" + Extension).OrderByDescending(f => f.Name, StringComparer.Ordinal).ToList();
        var removed = new List<string>();
        foreach (var f in files.Skip(keep))
        {
            f.Delete();
            TryDelete(f.FullName + ".sha256");
            removed.Add(f.FullName);
        }
        return removed;
    }

    private static async Task<(int Schema, long Transactions, long Audit)> InspectAsync(string path, CancellationToken ct)
    {
        await using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await c.OpenAsync(ct).ConfigureAwait(false);
        var check = await c.ExecuteScalarAsync<string>("PRAGMA integrity_check").ConfigureAwait(false);
        if (!string.Equals(check, "ok", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Database integrity check failed: " + check);
        var schema = await c.ExecuteScalarAsync<int?>("SELECT MAX(version) FROM schema_migrations").ConfigureAwait(false) ?? 0;
        var txns = await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM weighment_transactions").ConfigureAwait(false);
        var audit = await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM audit_events").ConfigureAwait(false);
        return (schema, txns, audit);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var s = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(s, ct).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
