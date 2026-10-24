using Dapper;
using Microsoft.Data.Sqlite;
using Npgsql;
using Weighbridge.Core.Security;
using Weighbridge.Data.Stores;

namespace Weighbridge.Data.Tests;

/// <summary>
/// Creates throw-away databases. SQLite always; PostgreSQL when WB_TEST_PG holds an admin connection string
/// (e.g. "Host=localhost;Username=postgres;Password=…;Database=postgres"), as in CI.
/// </summary>
public static class TestDatabases
{
    public static string? PostgresAdmin => Environment.GetEnvironmentVariable("WB_TEST_PG");

    public static IEnumerable<object[]> Providers()
    {
        yield return ["sqlite"];
        if (!string.IsNullOrWhiteSpace(PostgresAdmin)) yield return ["postgres"];
    }

    public static async Task<TestDb> CreateAsync(string provider, bool migrate = true)
    {
        Database db;
        Func<Task> cleanup;
        if (provider == "sqlite")
        {
            var path = Path.Combine(Path.GetTempPath(), $"awms-test-{Guid.NewGuid():N}.db");
            db = new Database(DatabaseOptions.SqliteFile(path));
            cleanup = () =>
            {
                SqliteConnection.ClearAllPools();
                foreach (var f in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(f)) File.Delete(f);
                return Task.CompletedTask;
            };
        }
        else
        {
            var name = "awms_test_" + Guid.NewGuid().ToString("N")[..12];
            await using (var admin = new NpgsqlConnection(PostgresAdmin))
            {
                await admin.OpenAsync();
                await admin.ExecuteAsync($"CREATE DATABASE {name}");
            }
            var cs = new NpgsqlConnectionStringBuilder(PostgresAdmin) { Database = name }.ToString();
            db = new Database(new DatabaseOptions(DatabaseProvider.PostgreSql, cs));
            cleanup = async () =>
            {
                NpgsqlConnection.ClearAllPools();
                await using var admin = new NpgsqlConnection(PostgresAdmin);
                await admin.OpenAsync();
                await admin.ExecuteAsync($"DROP DATABASE IF EXISTS {name} WITH (FORCE)");
            };
        }
        if (migrate) await MigrationRunner.MigrateAsync(db);
        return new TestDb(db, provider, cleanup);
    }
}

public sealed class TestDb(Database db, string provider, Func<Task> cleanup) : IAsyncDisposable
{
    public Database Db { get; } = db;
    public string Provider { get; } = provider;
    public DateTimeOffset Now { get; set; } = new(2026, 3, 1, 8, 0, 0, TimeSpan.Zero);

    public ValueTask DisposeAsync() => new(cleanup());

    /// <summary>Roles, an admin user, one site and one weighbridge.</summary>
    public async Task<(long UserId, long SiteId, long WeighbridgeId)> SeedAsync()
    {
        return await Db.InTransactionAsync(async s =>
        {
            var roleId = 0L;
            foreach (var (role, perms) in Roles.Defaults)
            {
                var id = await UserStore.EnsureRoleAsync(s, role, null, perms);
                if (role == Roles.Administrator) roleId = id;
            }
            var userId = await UserStore.InsertAsync(s, new UserEntity
            {
                Username = "Admin", DisplayName = "Administrator", PasswordHash = PasswordHasher.Hash("x-Admin-2026", 20_000), RoleId = roleId,
                CreatedUtc = Now, UpdatedUtc = Now,
            });
            var siteId = await SiteStore.InsertSiteAsync(s, new SiteEntity { Code = "CMB", Name = "Colombo Yard" });
            var wbId = await SiteStore.InsertWeighbridgeAsync(s, new WeighbridgeEntity { SiteId = siteId, Code = "WB1", Name = "Bridge 1", CapacityG = 80_000_000, MinCaptureG = 200_000 });
            return (userId, siteId, wbId);
        });
    }
}
