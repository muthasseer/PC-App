using Dapper;
using Weighbridge.Core.Indicators;
using Weighbridge.Core.Weighing;
using Weighbridge.Data.Stores;

namespace Weighbridge.Data.Tests;

public class MigrationTests
{
    [Theory]
    [MemberData(nameof(TestDatabases.Providers), MemberType = typeof(TestDatabases))]
    public async Task Migrations_apply_once_and_are_idempotent(string provider)
    {
        await using var t = await TestDatabases.CreateAsync(provider, migrate: false);
        var first = await MigrationRunner.MigrateAsync(t.Db);
        Assert.Equal(0, first.FromVersion);
        Assert.Contains("001_initial", first.Applied);
        var second = await MigrationRunner.MigrateAsync(t.Db);
        Assert.Empty(second.Applied);
        Assert.Equal(MigrationRunner.LatestVersion(t.Db.Options.Provider), await MigrationRunner.CurrentVersionAsync(t.Db));
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Providers), MemberType = typeof(TestDatabases))]
    public async Task Modified_or_newer_schema_is_refused(string provider)
    {
        await using var t = await TestDatabases.CreateAsync(provider);
        await using (var s = await t.Db.OpenAsync()) await s.ExecuteAsync("UPDATE schema_migrations SET checksum = 'tampered' WHERE version = 1");
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => MigrationRunner.MigrateAsync(t.Db));
        Assert.Contains("checksum", e.Message);

        await using (var s = await t.Db.OpenAsync())
        {
            await s.ExecuteAsync("UPDATE schema_migrations SET checksum = @c WHERE version = 1", new { c = MigrationRunner.Load(t.Db.Options.Provider)[0].Checksum });
            await s.ExecuteAsync("INSERT INTO schema_migrations (version, name, applied_utc, checksum) VALUES (999, 'future', '2030-01-01T00:00:00.000Z', 'x')");
        }
        e = await Assert.ThrowsAsync<InvalidOperationException>(() => MigrationRunner.MigrateAsync(t.Db));
        Assert.Contains("newer", e.Message);
    }
}

public class UserAndMasterDataTests
{
    [Theory]
    [MemberData(nameof(TestDatabases.Providers), MemberType = typeof(TestDatabases))]
    public async Task Users_roles_and_permissions(string provider)
    {
        await using var t = await TestDatabases.CreateAsync(provider);
        var (userId, _, _) = await t.SeedAsync();
        await using var s = await t.Db.OpenAsync();
        var user = await UserStore.FindByUsernameAsync(s, "admin");
        Assert.NotNull(user);
        Assert.Equal(userId, user!.Id);
        Assert.Equal("Administrator", user.RoleName);
        Assert.True(user.IsActive);
        var perms = await UserStore.PermissionsAsync(s, user.RoleId);
        Assert.Contains(Core.Security.Permissions.UsersManage, perms);
        var roles = await UserStore.RolesAsync(s);
        Assert.Equal(5, roles.Count);
        Assert.DoesNotContain(Core.Security.Permissions.WeighingCapture, roles.Single(r => r.Name == "Auditor").Permissions);

        await UserStore.RecordLoginFailureAsync(s, userId, 3, t.Now.AddMinutes(15));
        user = await UserStore.GetAsync(s, userId);
        Assert.Equal(3, user!.FailedAttempts);
        Assert.Equal(t.Now.AddMinutes(15), user.LockedUntilUtc);
        await UserStore.AddLoginHistoryAsync(s, new LoginHistoryEntity { UserId = userId, Username = "Admin", Success = false, Reason = "bad password", AtUtc = t.Now });
        Assert.Single(await UserStore.LoginHistoryAsync(s));
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Providers), MemberType = typeof(TestDatabases))]
    public async Task Vehicles_parties_materials(string provider)
    {
        await using var t = await TestDatabases.CreateAsync(provider);
        var (userId, _, _) = await t.SeedAsync();
        await t.Db.InTransactionAsync(async s =>
        {
            await MasterDataStore.InsertVehicleAsync(s, new VehicleEntity { Registration = "  wp cab-1234 ", RfidTag = "E200ABCD", CreatedUtc = t.Now, UpdatedUtc = t.Now });
            await MasterDataStore.InsertVehicleAsync(s, new VehicleEntity { Registration = "NC LK-5521", CreatedUtc = t.Now, UpdatedUtc = t.Now });
            await MasterDataStore.InsertPartyAsync(s, PartyKind.Customer, new PartyEntity { Code = "C001", Name = "Lanka Cement", CreatedUtc = t.Now, UpdatedUtc = t.Now });
            await MasterDataStore.InsertPartyAsync(s, PartyKind.Supplier, new PartyEntity { Code = "S001", Name = "River Sand Ltd", CreatedUtc = t.Now, UpdatedUtc = t.Now });
            await MasterDataStore.InsertMaterialAsync(s, new MaterialEntity { Code = "M-SAND", Name = "Manufactured sand", Category = "Aggregates", CreatedUtc = t.Now, UpdatedUtc = t.Now });
            return 0;
        });

        await using var r = await t.Db.OpenAsync();
        var v = await MasterDataStore.VehicleByRegistrationAsync(r, "WPCAB1234");
        Assert.Equal("WP CAB-1234", v!.Registration);
        Assert.Equal("WP CAB-1234", (await MasterDataStore.VehicleByTagAsync(r, "E200ABCD"))!.Registration);
        Assert.Equal(1, (await MasterDataStore.SearchVehiclesAsync(r, "cab 12")).Total);
        Assert.Equal(2, (await MasterDataStore.SearchVehiclesAsync(r, null)).Total);
        Assert.Single((await MasterDataStore.SearchPartiesAsync(r, PartyKind.Customer, "lanka")).Items);
        Assert.Single((await MasterDataStore.SearchPartiesAsync(r, PartyKind.Supplier, "s001")).Items);
        Assert.Equal("Manufactured sand", (await MasterDataStore.SearchMaterialsAsync(r, "aggreg")).Items.Single().Name);

        // Duplicate registrations (different spelling) are rejected by the unique normalised key.
        await Assert.ThrowsAnyAsync<System.Data.Common.DbException>(() => t.Db.InTransactionAsync(s =>
            MasterDataStore.InsertVehicleAsync(s, new VehicleEntity { Registration = "WP-CAB 1234", CreatedUtc = t.Now, UpdatedUtc = t.Now })));

        // Stored tare: unusable until approved.
        await t.Db.InTransactionAsync(async s =>
        {
            await MasterDataStore.SetStoredTareAsync(s, v.Id, 12_300_000, "Measured", null, userId, approvedBy: null, t.Now, t.Now.AddDays(90));
            return 0;
        });
        v = await MasterDataStore.VehicleAsync(r, v.Id);
        Assert.False(v!.HasUsableStoredTare(t.Now));
        await MasterDataStore.ApproveStoredTareAsync(r, v.Id, userId, t.Now, t.Now.AddDays(90));
        v = await MasterDataStore.VehicleAsync(r, v.Id);
        Assert.True(v!.HasUsableStoredTare(t.Now.AddDays(1)));
        Assert.False(v.HasUsableStoredTare(t.Now.AddDays(91)));
        Assert.Equal(12300m, v.StoredTare!.Value.Kilograms);
        Assert.Single(await MasterDataStore.TareHistoryAsync(r, v.Id));
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Providers), MemberType = typeof(TestDatabases))]
    public async Task Profiles_are_versioned(string provider)
    {
        await using var t = await TestDatabases.CreateAsync(provider);
        var (userId, _, wbId) = await t.SeedAsync();
        var profile = new IndicatorProfile { Name = "XK3190", Manufacturer = "Yaohua" };
        var id = await t.Db.InTransactionAsync(s => SiteStore.SaveProfileAsync(s, profile, userId, t.Now));
        await t.Db.InTransactionAsync(s => SiteStore.SaveProfileAsync(s, profile.NextVersion() with { Notes = "9600 7E1" }, userId, t.Now));
        await Assert.ThrowsAsync<ConcurrencyException>(() => t.Db.InTransactionAsync(s => SiteStore.SaveProfileAsync(s, profile, userId, t.Now)));
        await t.Db.InTransactionAsync(async s => { await SiteStore.AssignProfileAsync(s, wbId, id, "COM4", t.Now); return 0; });

        await using var r = await t.Db.OpenAsync();
        var stored = await SiteStore.GetProfileAsync(r, id);
        Assert.Equal(2, stored!.Version);
        Assert.Equal("9600 7E1", stored.ToProfile().Notes);
        var v1 = await SiteStore.ProfileVersionJsonAsync(r, profile.Id, 1);
        Assert.Null(IndicatorProfile.FromJson(v1!).Notes);
        var device = await SiteStore.DeviceForWeighbridgeAsync(r, wbId);
        Assert.Equal("COM4", device!.PortOverride);
    }
}

public class TransactionTests
{
    private static MeasurementEntity Measurement(long txnId, int stage, long grams, long userId, DateTimeOffset at, MeasurementSource source = MeasurementSource.Indicator, string? manualReason = null) => new()
    {
        TransactionId = txnId, Stage = stage, Source = source, WeightG = grams, ReportedValue = (grams / 1000m).ToString(System.Globalization.CultureInfo.InvariantCulture),
        IsStable = true, CapturedUtc = at, OperatingMode = OperatingMode.SemiAutomatic, OperatorId = userId, ProfileName = "XK3190", ProfileVersion = 2,
        RawText = "ST,GS,+032500.0kg", ManualReason = manualReason,
    };

    private static async Task<long> CreatePendingAsync(TestDb t, long userId, long siteId, long wbId, string vehicle, long firstG, string? customer = null)
    {
        return await t.Db.InTransactionAsync(async s =>
        {
            var seq = await TransactionStore.NextSequenceAsync(s, "ticket|CMB|*|2026");
            var txn = new TransactionEntity
            {
                TicketNo = $"CMB-2026-{seq:000000}", SiteId = siteId, WeighbridgeId = wbId, Status = TransactionStatus.Pending, Direction = TransactionDirection.Inbound,
                OperatingMode = OperatingMode.SemiAutomatic, VehicleReg = vehicle, VehicleRegNorm = VehicleRegistration.Normalize(vehicle), CustomerName = customer,
                MaterialName = "Sand", CreatedBy = userId, CreatedUtc = t.Now, UpdatedUtc = t.Now,
            };
            var id = await TransactionStore.InsertAsync(s, txn);
            var mId = await TransactionStore.InsertMeasurementAsync(s, Measurement(id, 1, firstG, userId, t.Now));
            await TransactionStore.InsertRawFramesAsync(s, mId, [new RawFrameEntity { Seq = 41, ReceivedUtc = t.Now, Hex = "5354", Display = "ST", IsCaptured = true }]);
            await TransactionStore.SetFirstMeasurementAsync(s, id, mId, firstG, manual: false, overridden: false, t.Now, expectedVersion: 1);
            return id;
        });
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Providers), MemberType = typeof(TestDatabases))]
    public async Task First_and_second_weighing_with_integrity_rules(string provider)
    {
        await using var t = await TestDatabases.CreateAsync(provider);
        var (userId, siteId, wbId) = await t.SeedAsync();
        var id = await CreatePendingAsync(t, userId, siteId, wbId, "WP CAB-1234", 32_500_000);

        // Only one pending transaction per vehicle and site.
        await Assert.ThrowsAnyAsync<System.Data.Common.DbException>(() => CreatePendingAsync(t, userId, siteId, wbId, "wp-cab 1234", 30_000_000));

        await using (var r = await t.Db.OpenAsync())
        {
            var pending = await TransactionStore.PendingForVehicleAsync(r, siteId, "WPCAB1234");
            Assert.Equal(id, pending!.Id);
            Assert.Equal(2, pending.Version);
            Assert.Equal(32500m, pending.First!.Value.Kilograms);
            Assert.Equal("CMB-2026-000001", pending.TicketNo);
        }

        t.Now = t.Now.AddMinutes(40);
        await t.Db.InTransactionAsync(async s =>
        {
            var m2 = await TransactionStore.InsertMeasurementAsync(s, Measurement(id, 2, 12_300_000, userId, t.Now));
            await TransactionStore.CompleteAsync(s, new TransactionStore.Completion(id, m2, 12_300_000, 32_500_000, 12_300_000, 20_200_000, true, TareMethod.Measured,
                false, false, userId, t.Now, ExpectedVersion: 2));
            return 0;
        });

        // A stale version cannot complete twice.
        await Assert.ThrowsAsync<ConcurrencyException>(() => t.Db.InTransactionAsync(async s =>
        {
            await TransactionStore.CompleteAsync(s, new TransactionStore.Completion(id, null, null, 1, 1, 0, true, TareMethod.Measured, false, false, userId, t.Now, 2));
            return 0;
        }));

        await using var db = await t.Db.OpenAsync();
        var done = await TransactionStore.GetAsync(db, id);
        Assert.Equal(TransactionStatus.Completed, done!.Status);
        Assert.Equal(20200m, done.Net!.Value.Kilograms);
        Assert.Equal(TareMethod.Measured, done.TareMethod);
        Assert.True(done.FirstIsGross);
        Assert.Equal("Administrator", done.CreatedByName);
        var measurements = await TransactionStore.MeasurementsAsync(db, id);
        Assert.Equal(2, measurements.Count);
        Assert.Equal("XK3190", measurements[0].ProfileName);
        Assert.Single(await TransactionStore.RawFramesAsync(db, measurements[0].Id));

        // Database-level protection, independent of the application code.
        await Assert.ThrowsAnyAsync<System.Data.Common.DbException>(() => db.ExecuteAsync("UPDATE weighment_transactions SET net_g = 1 WHERE id = @id", new { id }));
        await Assert.ThrowsAnyAsync<System.Data.Common.DbException>(() => db.ExecuteAsync("DELETE FROM weighment_transactions WHERE id = @id", new { id }));
        await Assert.ThrowsAnyAsync<System.Data.Common.DbException>(() => db.ExecuteAsync("UPDATE weight_measurements SET weight_g = 1"));
        await Assert.ThrowsAnyAsync<System.Data.Common.DbException>(() => db.ExecuteAsync("DELETE FROM weight_measurements"));
        await Assert.ThrowsAnyAsync<System.Data.Common.DbException>(() => db.ExecuteAsync("DELETE FROM raw_frames"));

        // Completed transactions still accept detail corrections (logged), but cancelled ones are frozen.
        await db.ExecuteAsync("UPDATE weighment_transactions SET remarks = 'corrected PO number' WHERE id = @id", new { id });
        await t.Db.InTransactionAsync(async s =>
        {
            await TransactionStore.CancelAsync(s, id, userId, "Duplicate ticket", t.Now, expectedVersion: 3);
            return 0;
        });
        await Assert.ThrowsAnyAsync<System.Data.Common.DbException>(() => db.ExecuteAsync("UPDATE weighment_transactions SET remarks = 'x' WHERE id = @id", new { id }));
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Providers), MemberType = typeof(TestDatabases))]
    public async Task Database_rejects_inconsistent_rows(string provider)
    {
        await using var t = await TestDatabases.CreateAsync(provider);
        var (userId, siteId, wbId) = await t.SeedAsync();
        var id = await CreatePendingAsync(t, userId, siteId, wbId, "AAA-1", 20_000_000);
        await using var db = await t.Db.OpenAsync();
        // Completed without weights, cancelled without reason, manual measurement without reason.
        await Assert.ThrowsAnyAsync<System.Data.Common.DbException>(() => db.ExecuteAsync("UPDATE weighment_transactions SET status = 'Completed', completed_utc = 'x' WHERE id = @id", new { id }));
        await Assert.ThrowsAnyAsync<System.Data.Common.DbException>(() => db.ExecuteAsync("UPDATE weighment_transactions SET status = 'Cancelled', cancelled_utc = 'x' WHERE id = @id", new { id }));
        await Assert.ThrowsAnyAsync<System.Data.Common.DbException>(() => t.Db.InTransactionAsync(s =>
            TransactionStore.InsertMeasurementAsync(s, Measurement(id, 2, 10_000_000, userId, t.Now, MeasurementSource.Manual, manualReason: null))));
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Providers), MemberType = typeof(TestDatabases))]
    public async Task History_search_filters_and_paging(string provider)
    {
        await using var t = await TestDatabases.CreateAsync(provider);
        var (userId, siteId, wbId) = await t.SeedAsync();
        for (var i = 0; i < 25; i++)
        {
            t.Now = new DateTimeOffset(2026, 3, 1 + i % 5, 8 + i % 8, 0, 0, TimeSpan.Zero);
            await CreatePendingAsync(t, userId, siteId, wbId, $"TRK-{i:00}", 10_000_000 + i * 1_000_000, customer: i % 2 == 0 ? "Lanka Cement" : "Ceylon Steel");
        }

        await using var s = await t.Db.OpenAsync();
        var all = await TransactionStore.SearchAsync(s, new TransactionFilter { PageSize = 10 });
        Assert.Equal(25, all.Total);
        Assert.Equal(10, all.Items.Count);
        Assert.Equal(3, all.PageCount);
        var page3 = await TransactionStore.SearchAsync(s, new TransactionFilter { PageSize = 10, Page = 3 });
        Assert.Equal(5, page3.Items.Count);

        Assert.Equal(5, (await TransactionStore.SearchAsync(s, new TransactionFilter { From = new(2026, 3, 2, 0, 0, 0, TimeSpan.Zero), To = new(2026, 3, 3, 0, 0, 0, TimeSpan.Zero) })).Total);
        Assert.Single((await TransactionStore.SearchAsync(s, new TransactionFilter { Vehicle = "trk 07" })).Items);
        Assert.Equal(13, (await TransactionStore.SearchAsync(s, new TransactionFilter { Text = "lanka" })).Total);
        Assert.Equal(6, (await TransactionStore.SearchAsync(s, new TransactionFilter { FirstMinKg = 15000, FirstMaxKg = 20000 })).Total);
        Assert.Equal(25, (await TransactionStore.SearchAsync(s, new TransactionFilter { Status = TransactionStatus.Pending, Mode = OperatingMode.SemiAutomatic })).Total);
        Assert.Equal(0, (await TransactionStore.SearchAsync(s, new TransactionFilter { Status = TransactionStatus.Completed })).Total);
        Assert.Equal(25, (await TransactionStore.SearchAsync(s, new TransactionFilter { ProfileName = "xk31" })).Total);
        Assert.Equal(0, (await TransactionStore.SearchAsync(s, new TransactionFilter { HasManualWeight = true })).Total);
        Assert.Single((await TransactionStore.SearchAsync(s, new TransactionFilter { TicketNo = "000007" })).Items);
        // LIKE wildcards typed by the user are matched literally, not as patterns.
        Assert.Equal(0, (await TransactionStore.SearchAsync(s, new TransactionFilter { Text = "%" })).Total);
        Assert.Equal(0, (await TransactionStore.SearchAsync(s, new TransactionFilter { TicketNo = "_" })).Total);

        var ascending = await TransactionStore.SearchAsync(s, new TransactionFilter { Sort = TransactionSort.Vehicle, Descending = false, PageSize = 3 });
        Assert.Equal(["TRK-00", "TRK-01", "TRK-02"], ascending.Items.Select(x => x.VehicleReg));
        var streamed = new List<TransactionEntity>();
        await foreach (var x in TransactionStore.StreamAsync(s, new TransactionFilter())) streamed.Add(x);
        Assert.Equal(25, streamed.Count);
        Assert.Equal(25, (await TransactionStore.RecentAsync(s, 50)).Count);
    }
}

public class AuditTests
{
    [Theory]
    [MemberData(nameof(TestDatabases.Providers), MemberType = typeof(TestDatabases))]
    public async Task Hash_chain_detects_tampering_even_with_database_access(string provider)
    {
        await using var t = await TestDatabases.CreateAsync(provider);
        var (userId, _, _) = await t.SeedAsync();
        for (var i = 0; i < 5; i++)
        {
            await t.Db.InTransactionAsync(s => AuditStore.AppendAsync(s, new AuditEntry("Weighing", "Capture", $"Captured {i}", userId, "Admin", "Transaction", i.ToString(), Reason: "test"), t.Now.AddSeconds(i)));
        }
        await using var db = await t.Db.OpenAsync();
        var ok = await AuditStore.VerifyAsync(db);
        Assert.True(ok.Valid, ok.Message);
        Assert.Equal(5, ok.Checked);
        Assert.Equal(5, (await AuditStore.QueryAsync(db, new AuditFilter { Category = "Weighing" })).Total);
        Assert.Single(await AuditStore.ForEntityAsync(db, "Transaction", "3"));

        // Normal clients cannot alter audit rows.
        await Assert.ThrowsAnyAsync<System.Data.Common.DbException>(() => db.ExecuteAsync("UPDATE audit_events SET summary = 'x'"));

        // A database administrator who disables the protection is still detected by the hash chain.
        if (provider == "sqlite") await db.ExecuteAsync("DROP TRIGGER trg_audit_no_update");
        else await db.ExecuteAsync("ALTER TABLE audit_events DISABLE TRIGGER trg_audit_immutable");
        await db.ExecuteAsync("UPDATE audit_events SET summary = 'Captured 99' WHERE entity_id = '2'");
        var broken = await AuditStore.VerifyAsync(db);
        Assert.False(broken.Valid);
        Assert.Contains("altered", broken.Message);
    }

    [Fact]
    public async Task Audit_requires_transaction()
    {
        await using var t = await TestDatabases.CreateAsync("sqlite");
        await using var s = await t.Db.OpenAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => AuditStore.AppendAsync(s, new AuditEntry("x", "y", "z", null, "sys"), t.Now));
    }
}

public class MiscStoreTests
{
    [Theory]
    [MemberData(nameof(TestDatabases.Providers), MemberType = typeof(TestDatabases))]
    public async Task Settings_sync_queue_notifications(string provider)
    {
        await using var t = await TestDatabases.CreateAsync(provider);
        var (userId, _, _) = await t.SeedAsync();
        await using var s = await t.Db.OpenAsync();

        await SettingsStore.SetAsync(s, "company", new { Name = "Lanka Cement", Phone = "011" }, userId, t.Now);
        await SettingsStore.SetAsync(s, "company", new { Name = "Lanka Cement PLC", Phone = "011" }, userId, t.Now);
        Assert.Contains("PLC", await SettingsStore.GetRawAsync(s, "company"));
        Assert.Single(await SettingsStore.AllAsync(s));

        await SyncQueueStore.EnqueueAsync(s, "Transaction", "u1", 1, "{\"v\":1}", t.Now);
        await SyncQueueStore.EnqueueAsync(s, "Transaction", "u1", 2, "{\"v\":2}", t.Now);
        await SyncQueueStore.EnqueueAsync(s, "Transaction", "u2", 1, "{}", t.Now);
        var due = await SyncQueueStore.DueAsync(s, t.Now, 10);
        Assert.Equal(2, due.Count);
        Assert.Equal(2, due.Single(d => d.EntityUid == "u1").EntityVersion);
        await SyncQueueStore.MarkFailedAsync(s, due[0], "offline", t.Now.AddMinutes(1));
        await SyncQueueStore.MarkSentAsync(s, due[1], t.Now);
        Assert.Empty(await SyncQueueStore.DueAsync(s, t.Now, 10));
        Assert.Single(await SyncQueueStore.DueAsync(s, t.Now.AddMinutes(2), 10));
        var counts = await SyncQueueStore.CountsAsync(s);
        Assert.Equal(1, counts.Failed);
        Assert.Equal(1, counts.Sent);
        Assert.Equal(t.Now, counts.OldestPending);

        await NotificationStore.AddAsync(s, new NotificationEntity { AtUtc = t.Now, Severity = "Warning", Category = "Indicator", Title = "Disconnected", Message = "COM3" });
        Assert.Equal(1, await NotificationStore.UnreadCountAsync(s));
        await NotificationStore.MarkAllReadAsync(s);
        Assert.Equal(0, await NotificationStore.UnreadCountAsync(s));

        await DeviceEventStore.AddAsync(s, new DeviceEventEntity { AtUtc = t.Now, Device = "COM3", EventType = "Disconnected", Message = "cable" });
        Assert.Single(await DeviceEventStore.QueryAsync(s, t.Now.AddHours(-1), t.Now.AddHours(1)));
    }
}

public class SyncQueueRaceTests
{
    /// <summary>
    /// A transaction changes while its previous version is being uploaded. The upload result for the old version must
    /// not mark the row as sent, otherwise the newer version would never reach the cloud.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestDatabases.Providers), MemberType = typeof(TestDatabases))]
    public async Task Upload_result_for_an_older_version_does_not_hide_a_newer_one(string provider)
    {
        await using var t = await TestDatabases.CreateAsync(provider);
        await using var s = await t.Db.OpenAsync();
        await SyncQueueStore.EnqueueAsync(s, "Transaction", "u1", 1, "{\"status\":\"Pending\"}", t.Now);
        var inFlight = Assert.Single(await SyncQueueStore.DueAsync(s, t.Now, 10));

        // The second weighing completes the ticket while version 1 is on its way to the server.
        await SyncQueueStore.EnqueueAsync(s, "Transaction", "u1", 2, "{\"status\":\"Completed\"}", t.Now);
        Assert.False(await SyncQueueStore.MarkSentAsync(s, inFlight, t.Now));
        Assert.Equal(0, await SyncQueueStore.MarkFailedAsync(s, inFlight, "timeout", t.Now.AddHours(1)));
        Assert.False(await SyncQueueStore.MarkConflictAsync(s, inFlight, 1, "conflict", t.Now));

        var next = Assert.Single(await SyncQueueStore.DueAsync(s, t.Now, 10));
        Assert.Equal(2, next.EntityVersion);
        Assert.Contains("Completed", next.PayloadJson);
        Assert.True(await SyncQueueStore.MarkSentAsync(s, next, t.Now));
        Assert.Empty(await SyncQueueStore.DueAsync(s, t.Now, 10));
    }
}

public class BackupTests
{
    [Fact]
    public async Task Backup_verify_restore_and_prune()
    {
        await using var t = await TestDatabases.CreateAsync("sqlite");
        var (userId, siteId, wbId) = await t.SeedAsync();
        await t.Db.InTransactionAsync(s => AuditStore.AppendAsync(s, new AuditEntry("Test", "Before", "before backup", userId, "Admin"), t.Now));
        var dir = Directory.CreateTempSubdirectory("awms-backups");
        var evidence = Directory.CreateTempSubdirectory("awms-evidence");
        await File.WriteAllBytesAsync(Path.Combine(evidence.FullName, "a.jpg"), [0xFF, 0xD8, 0xFF]);
        try
        {
            var info = await SqliteBackup.CreateAsync(t.Db, dir.FullName, evidence.FullName, "1.0.0", t.Now);
            Assert.True(File.Exists(info.Path + ".sha256"));
            Assert.Equal(1, info.Manifest.AuditEvents);
            Assert.Equal(1, info.Manifest.EvidenceFiles);
            Assert.Equal(1, info.Manifest.SchemaVersion);
            var verified = await SqliteBackup.VerifyAsync(info.Path);
            Assert.Equal(info.Sha256, verified.Sha256);

            // Changes after the backup disappear on restore; the replaced database is kept.
            await t.Db.InTransactionAsync(s => AuditStore.AppendAsync(s, new AuditEntry("Test", "After", "after backup", userId, "Admin"), t.Now));
            var kept = await SqliteBackup.RestoreAsync(info.Path, t.Db.Options.SqlitePath!, evidence.FullName, t.Now);
            Assert.True(File.Exists(Path.Combine(kept, Path.GetFileName(t.Db.Options.SqlitePath!))));
            await using (var s = await t.Db.OpenAsync())
            {
                Assert.Equal(1, await s.QuerySingleAsync<long>("SELECT COUNT(*) FROM audit_events"));
                Assert.True((await AuditStore.VerifyAsync(s)).Valid);
            }

            // A corrupted archive is refused.
            var bytes = await File.ReadAllBytesAsync(info.Path);
            bytes[^10] ^= 0xFF;
            await File.WriteAllBytesAsync(info.Path, bytes);
            await Assert.ThrowsAsync<InvalidDataException>(() => SqliteBackup.VerifyAsync(info.Path));

            for (var i = 1; i <= 3; i++) await SqliteBackup.CreateAsync(t.Db, dir.FullName, null, "1.0.0", t.Now.AddMinutes(i));
            var removed = SqliteBackup.Prune(dir.FullName, keep: 2);
            Assert.Equal(2, removed.Count);
            Assert.Equal(2, Directory.GetFiles(dir.FullName, "*.awmsbak").Length);
        }
        finally
        {
            dir.Delete(true);
            evidence.Delete(true);
        }
    }
}
