using Weighbridge.Application.Reports;
using Weighbridge.Application.Security;
using Weighbridge.Application.Settings;
using Weighbridge.Application.Weighing;
using Weighbridge.Core.Security;
using Weighbridge.Core.Weighing;
using Weighbridge.Data;
using Weighbridge.Data.Stores;
using Weighbridge.Devices.Cameras;

namespace Weighbridge.Application.Tests;

public class AuthTests
{
    [Fact]
    public async Task Login_lockout_and_history()
    {
        await using var t = await TestHost.CreateAsync();
        Assert.Equal(LoginOutcome.InvalidCredentials, (await t.Host.Auth.LoginAsync("nobody", "x")).Outcome);
        for (var i = 0; i < 4; i++) Assert.Equal(LoginOutcome.InvalidCredentials, (await t.Host.Auth.LoginAsync("op", "wrong")).Outcome);
        var locked = await t.Host.Auth.LoginAsync("op", "wrong");
        Assert.Equal(LoginOutcome.LockedOut, locked.Outcome);
        // Even the right password is refused while locked.
        Assert.Equal(LoginOutcome.LockedOut, (await t.Host.Auth.LoginAsync("op", TestHost.Password)).Outcome);
        t.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal(LoginOutcome.Success, (await t.Host.Auth.LoginAsync("OP", TestHost.Password)).Outcome);
        var page = await t.Host.Audit.QueryAsync(t.Admin, new AuditFilter { Category = "Security" });
        Assert.Contains(page.Items, e => e.Action == "LockedOut");
    }

    [Fact]
    public async Task Mfa_enrolment_and_login()
    {
        await using var t = await TestHost.CreateAsync();
        var (secret, uri) = AuthService.BeginMfaEnrolment(t.Operator, "AWMS");
        Assert.StartsWith("otpauth://", uri);
        Assert.False(await t.Host.Auth.CompleteMfaEnrolmentAsync(t.Operator, secret, "000000"));
        Assert.True(await t.Host.Auth.CompleteMfaEnrolmentAsync(t.Operator, secret, Totp.Compute(secret, Totp.Step(t.Now))));
        Assert.Equal(LoginOutcome.MfaRequired, (await t.Host.Auth.LoginAsync("op", TestHost.Password)).Outcome);
        t.Advance(TimeSpan.FromSeconds(31));
        var ok = await t.Host.Auth.LoginAsync("op", TestHost.Password, Totp.Compute(secret, Totp.Step(t.Now)));
        Assert.Equal(LoginOutcome.Success, ok.Outcome);
    }

    [Fact]
    public async Task Permissions_and_supervisor_approval()
    {
        await using var t = await TestHost.CreateAsync();
        await Assert.ThrowsAsync<PermissionDeniedException>(() => t.Host.Auth.CreateUserAsync(t.Operator, "x", "x", TestHost.Password, Roles.Viewer));
        Assert.Null(await t.Host.Auth.VerifySupervisorAsync("op", TestHost.Password, Permissions.ApprovalsDecide, "test"));
        Assert.NotNull(await t.Host.Auth.VerifySupervisorAsync("mgr", TestHost.Password, Permissions.ApprovalsDecide, "test"));
        await Assert.ThrowsAsync<ArgumentException>(() => t.Host.Auth.CreateUserAsync(t.Admin, "weak", "Weak", "short", Roles.Viewer));
        await t.Host.Auth.ChangePasswordAsync(t.Operator, TestHost.Password, "NewPass#2026");
        Assert.Equal(LoginOutcome.Success, (await t.Host.Auth.LoginAsync("op", "NewPass#2026")).Outcome);
    }
}

public class WeighingFlowTests
{
    [Fact]
    public async Task First_and_second_weighing_produce_a_completed_audited_ticket()
    {
        await using var t = await TestHost.CreateAsync();
        var first = await t.CaptureAsync(t.Operator, "wp cab-1234", 12300);
        Assert.True(first.Success, first.Message);
        Assert.False(first.IsFinal);
        Assert.Equal("QRY-2026-000001", first.Transaction!.TicketNo);
        Assert.Equal(TransactionStatus.Pending, first.Transaction.Status);

        var ctx = await t.Host.Weighing.VehicleContextAsync("WP CAB 1234");
        Assert.Equal(WeighingStage.Second, ctx.NextStage);

        t.Advance(TimeSpan.FromMinutes(25));
        var second = await t.CaptureAsync(t.Operator, "WP CAB-1234", 32500);
        Assert.True(second.Success, second.Message);
        Assert.True(second.IsFinal);
        var done = second.Transaction!;
        Assert.Equal(TransactionStatus.Completed, done.Status);
        Assert.Equal(32500m, done.Gross!.Value.Kilograms);
        Assert.Equal(12300m, done.Tare!.Value.Kilograms);
        Assert.Equal(20200m, done.Net!.Value.Kilograms);
        Assert.Equal(TareMethod.Measured, done.TareMethod);
        Assert.Equal("River sand", done.MaterialName);

        var detail = await t.Host.History.DetailAsync(t.Admin, done.Id);
        Assert.Equal(2, detail.Measurements.Count);
        Assert.All(detail.Measurements, m => Assert.Equal("Test profile", m.ProfileName));
        Assert.Equal(2, detail.RawFrames[detail.Measurements[0].Id].Count);
        Assert.Single(detail.RawFrames[detail.Measurements[0].Id], f => f.IsCaptured);
        Assert.Contains(detail.Timeline, e => e.Kind == "Completed");
        Assert.True((await t.Host.Audit.VerifyAsync()).Valid);

        var sync = await t.Host.Database.ReadAsync(s => SyncQueueStore.CountsAsync(s));
        Assert.True(sync.Pending >= 1);
    }

    [Fact]
    public async Task Stale_and_unstable_readings_are_not_captured()
    {
        await using var t = await TestHost.CreateAsync();
        var stale = await t.Host.Weighing.CaptureAsync(t.Operator, new CaptureRequest { Details = t.Details("AAA-1"), Mode = OperatingMode.SemiAutomatic, Snapshot = t.Snapshot(15000, ageMs: 5000) });
        Assert.Equal(CaptureFailure.Invalid, stale.Failure);
        Assert.Contains("old", stale.Message);

        var unstable = await t.CaptureAsync(t.Operator, "AAA-1", 15000, stable: false);
        Assert.Equal(CaptureFailure.WaitForStability, unstable.Failure);

        // Supervisor override policy: refused without approval, accepted (and flagged) with it.
        var weighing = await t.Host.Settings.GetAsync<WeighingSettings>(SettingKeys.Weighing);
        await t.Host.Settings.SaveAsync(SettingKeys.Weighing, weighing with { Capture = weighing.Capture with { Unstable = UnstableCapturePolicy.SupervisorOverride } }, t.Admin);
        Assert.Equal(CaptureFailure.OverrideRequired, (await t.CaptureAsync(t.Operator, "AAA-1", 15000, stable: false)).Failure);
        var approved = await t.Host.Weighing.CaptureAsync(t.Operator, new CaptureRequest
        {
            Details = t.Details("AAA-1"), Mode = OperatingMode.SemiAutomatic, Snapshot = t.Snapshot(15000, stable: false),
            OverrideApproval = await t.ApprovalAsync(Permissions.WeighingOverrideUnstable), OverrideReason = "Wind on the platform",
        });
        Assert.True(approved.Success, approved.Message);
        Assert.True(approved.Measurement!.UnstableOverride);
        Assert.True(approved.Transaction!.HasOverride);
        Assert.NotNull(approved.Measurement.OverrideApprovedBy);
    }

    [Fact]
    public async Task Manual_entry_needs_reason_and_supervisor_and_is_flagged()
    {
        await using var t = await TestHost.CreateAsync();
        var noReason = await t.Host.Weighing.ManualEntryAsync(t.Operator, new ManualEntryRequest { Details = t.Details("MAN-1"), WeightKg = 30000, Reason = "" });
        Assert.Equal(CaptureFailure.MissingDetails, noReason.Failure);
        var noApproval = await t.Host.Weighing.ManualEntryAsync(t.Operator, new ManualEntryRequest { Details = t.Details("MAN-1"), WeightKg = 30000, Reason = "Indicator broken" });
        Assert.Equal(CaptureFailure.ApprovalRequired, noApproval.Failure);

        var approval = await t.ApprovalAsync(Permissions.ApprovalsDecide);
        var first = await t.Host.Weighing.ManualEntryAsync(t.Operator, new ManualEntryRequest { Details = t.Details("MAN-1"), WeightKg = 30000, Reason = "Indicator broken", Approval = approval });
        Assert.True(first.Success, first.Message);
        Assert.Equal(MeasurementSource.Manual, first.Measurement!.Source);
        var second = await t.Host.Weighing.ManualEntryAsync(t.Operator, new ManualEntryRequest { Details = t.Details("MAN-1"), WeightKg = 11000, Reason = "Indicator broken", Approval = approval });
        Assert.True(second.Transaction!.HasManualWeight);
        Assert.Equal(TareMethod.ManualTare, second.Transaction.TareMethod);

        var ticket = await t.Host.Tickets.BuildAsync(second.Transaction.Id);
        Assert.True(ticket.HasManualWeight);
        Assert.Equal("Manual entry", ticket.First!.SourceLabel);
        var notes = await t.Host.Notifications.RecentAsync();
        Assert.Contains(notes, n => n.Category == "ManualWeight");
        var report = await t.Host.Reports.BuildAsync(t.Admin, new ReportRequest(ReportKind.ManualOverride, new TransactionFilter()));
        Assert.Single(report.Rows);
    }

    [Fact]
    public async Task Duplicate_blacklist_and_stored_tare()
    {
        await using var t = await TestHost.CreateAsync();
        await t.CaptureAsync(t.Operator, "DUP-1", 10000);
        await t.CaptureAsync(t.Operator, "DUP-1", 25000);
        t.Advance(TimeSpan.FromMinutes(3));
        var dup = await t.CaptureAsync(t.Operator, "DUP-1", 10000);
        Assert.Equal(CaptureFailure.DuplicateSuspected, dup.Failure);
        Assert.True((await t.CaptureAsync(t.Operator, "DUP-1", 10000, confirmDuplicate: true)).Success);

        var vehicleId = await t.Host.MasterData.SaveVehicleAsync(t.Admin, new VehicleEntity { Registration = "BAD-9" });
        await t.Host.MasterData.SetBlacklistAsync(t.Manager, vehicleId, true, "Overloading incidents");
        var blocked = await t.CaptureAsync(t.Operator, "BAD 9", 12000);
        Assert.Equal(CaptureFailure.Blacklisted, blocked.Failure);
        Assert.Contains("Overloading", blocked.Message);

        var tareVehicle = await t.Host.MasterData.SaveVehicleAsync(t.Admin, new VehicleEntity { Registration = "TARE-1" });
        await t.Host.MasterData.SetStoredTareAsync(t.Manager, tareVehicle, 9800, "Measured", null, 90);
        var single = await t.CaptureAsync(t.Operator, "TARE-1", 28000, storedTare: true);
        Assert.True(single.IsFinal, single.Message);
        Assert.Equal(TareMethod.StoredTare, single.Transaction!.TareMethod);
        Assert.Equal(18200m, single.Transaction.Net!.Value.Kilograms);
    }

    [Fact]
    public async Task Cancel_edit_correct_and_reissue_keep_full_traceability()
    {
        await using var t = await TestHost.CreateAsync();
        var printer = new RecordingPrinter();
        var a = (await t.CaptureAsync(t.Operator, "TRK-1", 12000)).Transaction!;
        await Assert.ThrowsAsync<PermissionDeniedException>(() => t.Host.Weighing.CancelAsync(t.Operator, a.Id, "Wrong vehicle", null));

        // Correct the first weight of a pending ticket: the original measurement stays, linked.
        var corrected = await t.Host.Weighing.CorrectFirstWeightAsync(t.Manager, a.Id, 12100, "Calibration offset", await t.ApprovalAsync(Permissions.ApprovalsDecide));
        Assert.Equal(12100m, corrected.First!.Value.Kilograms);
        var completed = (await t.CaptureAsync(t.Operator, "TRK-1", 30000)).Transaction!;
        Assert.Equal(17900m, completed.Net!.Value.Kilograms);
        var ms = (await t.Host.History.DetailAsync(t.Admin, a.Id)).Measurements;
        Assert.Equal(3, ms.Count);
        Assert.Equal(ms[0].Id, ms[1].CorrectsMeasurementId);

        // Edit details of a completed ticket: reason required, every field change logged.
        await Assert.ThrowsAsync<ArgumentException>(() => t.Host.Weighing.UpdateDetailsAsync(t.Manager, a.Id, t.Details("TRK-1") with { ReferenceNo = "PO-1" }, null, null));
        await t.Host.Weighing.UpdateDetailsAsync(t.Manager, a.Id, t.Details("TRK-1") with { ReferenceNo = "PO-1" }, "Customer PO added", null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => t.Host.Weighing.UpdateDetailsAsync(t.Manager, a.Id, t.Details("OTHER-1"), "change vehicle", null));
        var detail = await t.Host.History.DetailAsync(t.Admin, a.Id);
        Assert.Contains(detail.Changes, c => c.Field == "Reference" && c.NewValue == "PO-1");

        // Print, reprint (reason required), then re-issue with a corrected second weight.
        await t.Host.Tickets.PrintAsync(t.Operator, a.Id, printer);
        await Assert.ThrowsAsync<PermissionDeniedException>(() => t.Host.Tickets.PrintAsync(t.Operator, a.Id, printer, "lost"));
        var reprint = await t.Host.Tickets.PrintAsync(t.Manager, a.Id, printer, "Driver lost the ticket");
        Assert.True(reprint.IsReprint);
        Assert.Equal(1, printer.Printed[^1].Data.ReprintNumber);
        Assert.True(File.Exists(reprint.PdfPath));

        var replacement = await t.Host.Weighing.ReissueAsync(t.Manager, a.Id, null, 30100, "Second weighing taken with trailer hitch on", await t.ApprovalAsync(Permissions.ApprovalsDecide));
        Assert.Equal(18000m, replacement.Net!.Value.Kilograms);
        Assert.Equal(a.Id, replacement.ReplacesTransactionId);
        var original = await t.Host.History.DetailAsync(t.Admin, a.Id);
        Assert.Equal(TransactionStatus.Cancelled, original.Transaction.Status);
        Assert.Equal(replacement.Id, original.ReplacedBy!.Id);
        Assert.Contains("Corrected", original.Transaction.CancelReason);
        Assert.Equal(2, (await t.Host.History.DetailAsync(t.Admin, replacement.Id)).Measurements.Count(m => m.CorrectsMeasurementId is not null));

        // Cancel with supervisor authorisation; the ticket QR no longer verifies as valid.
        var b = (await t.CaptureAsync(t.Operator, "TRK-2", 15000)).Transaction!;
        var payload = (await t.Host.Tickets.BuildAsync(b.Id)).VerificationPayload!;
        Assert.True((await t.Host.Tickets.VerifyAsync(payload)).Valid);
        await t.Host.Weighing.CancelAsync(t.Manager, b.Id, "Driver left without second weighing", null);
        var check = await t.Host.Tickets.VerifyAsync(payload);
        Assert.False(check.Valid);
        Assert.Contains("cancelled", check.Message);
        Assert.True((await t.Host.Audit.VerifyAsync()).Valid);
    }

    [Fact]
    public async Task Camera_evidence_is_stored_with_hash_and_tamper_is_detected()
    {
        await using var t = await TestHost.CreateAsync();
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4 };
        var r = await t.Host.Weighing.CaptureAsync(t.Operator, new CaptureRequest
        {
            Details = t.Details("CAM-1"), Mode = OperatingMode.SemiAutomatic, Snapshot = t.Snapshot(14000),
            Images = [new CameraSnapshot("front", "Front", jpeg, "image/jpeg", t.Now)],
        });
        var detail = await t.Host.History.DetailAsync(t.Admin, r.Transaction!.Id);
        var evidence = Assert.Single(detail.Evidence);
        Assert.True((await t.Host.History.ReadEvidenceAsync(evidence)).IntegrityOk);
        await File.WriteAllBytesAsync(Path.Combine(t.Host.Paths.EvidenceDirectory, evidence.FilePath), [0xFF, 0xD8, 0xFF, 9]);
        var tampered = await t.Host.History.ReadEvidenceAsync(evidence);
        Assert.False(tampered.IntegrityOk);
    }

    [Fact]
    public async Task History_reports_and_exports()
    {
        await using var t = await TestHost.CreateAsync();
        for (var i = 0; i < 6; i++)
        {
            await t.CaptureAsync(t.Operator, $"R-{i}", 10000 + i * 100);
            t.Advance(TimeSpan.FromMinutes(10));
            await t.CaptureAsync(t.Operator, $"R-{i}", 30000);
            t.Advance(TimeSpan.FromHours(5));
        }
        var page = await t.Host.History.SearchAsync(t.Operator, new TransactionFilter { Status = TransactionStatus.Completed, PageSize = 4 });
        Assert.Equal(6, page.Total);
        Assert.Equal(4, page.Items.Count);

        var daily = await t.Host.Reports.BuildAsync(t.Manager, new ReportRequest(ReportKind.DailySummary, new TransactionFilter()));
        Assert.Equal(6L, daily.Rows.Sum(r => (long)r[1]!));
        var material = await t.Host.Reports.BuildAsync(t.Manager, new ReportRequest(ReportKind.MaterialWise, new TransactionFilter()));
        Assert.Equal("River sand", material.Rows.Single()[0]);

        await Assert.ThrowsAsync<PermissionDeniedException>(() => t.Host.Reports.ExportAsync(t.Operator, new ReportRequest(ReportKind.Transactions, new TransactionFilter()), ExportFormat.Csv));
        foreach (var format in Enum.GetValues<ExportFormat>())
        {
            var export = await t.Host.Reports.ExportAsync(t.Manager, new ReportRequest(ReportKind.Transactions, new TransactionFilter()), format);
            Assert.Equal(6, export.Rows);
            Assert.True(export.Content.Length > 100);
        }
        await Assert.ThrowsAsync<PermissionDeniedException>(() => t.Host.Reports.BuildAsync(t.Operator, new ReportRequest(ReportKind.AuditTrail, new TransactionFilter())));
        var auditReport = await t.Host.Reports.BuildAsync(t.Admin, new ReportRequest(ReportKind.AuditTrail, new TransactionFilter()));
        Assert.Contains(auditReport.Rows, r => (string)r[4]! == "Exported");
    }

    [Fact]
    public async Task Backups_manual_and_scheduled()
    {
        await using var t = await TestHost.CreateAsync();
        await t.CaptureAsync(t.Operator, "BK-1", 12000);
        await Assert.ThrowsAsync<PermissionDeniedException>(() => t.Host.Backups.BackupNowAsync(t.Operator));
        var info = await t.Host.Backups.BackupNowAsync(t.Admin);
        Assert.Equal(1, info.Manifest.Transactions);
        Assert.Equal(info.Sha256, (await t.Host.Backups.VerifyAsync(info.Path)).Sha256);

        // The clock starts at 02:30 (UTC in CI), before the default 23:30 schedule; a manual backup earlier in the
        // day does not replace the nightly one.
        Assert.False(await t.Host.Backups.RunScheduledIfDueAsync());
        t.Advance(TimeSpan.FromHours(21) + TimeSpan.FromMinutes(5));
        Assert.True(await t.Host.Backups.RunScheduledIfDueAsync());
        Assert.False(await t.Host.Backups.RunScheduledIfDueAsync()); // once per day
        Assert.Equal(2, (await t.Host.Backups.HistoryAsync()).Count(h => h.Status == "Succeeded"));
    }

    [Fact]
    public async Task Scheduled_backup_missed_while_the_pc_was_off_is_made_on_the_next_start()
    {
        await using var t = await TestHost.CreateAsync();
        // 02:30, no backup since yesterday's 23:30 slot (the PC was off): due straight away, then not again.
        Assert.True(await t.Host.Backups.RunScheduledIfDueAsync());
        t.Advance(TimeSpan.FromHours(1));
        Assert.False(await t.Host.Backups.RunScheduledIfDueAsync());
        // Two days off: one catch-up backup, not one per missed day.
        t.Advance(TimeSpan.FromDays(2));
        Assert.True(await t.Host.Backups.RunScheduledIfDueAsync());
        Assert.False(await t.Host.Backups.RunScheduledIfDueAsync());
        Assert.Equal(2, (await t.Host.Backups.HistoryAsync()).Count(h => h is { Status: "Succeeded", Kind: "Scheduled" }));
    }

    [Fact]
    public async Task Health_reports_problems()
    {
        await using var t = await TestHost.CreateAsync();
        var health = await t.Host.Health.CheckAsync(Core.Indicators.ConnectionState.Disconnected);
        Assert.True(health.DatabaseOk);
        Assert.Equal(1, health.SchemaVersion);
        Assert.Contains(health.Warnings, w => w.Contains("No backup"));
        Assert.Contains(health.Warnings, w => w.Contains("Indicator"));
    }
}

public class ProfileServiceTests
{
    [Fact]
    public async Task Profiles_are_versioned_activated_and_imported_unverified()
    {
        await using var t = await TestHost.CreateAsync();
        var profiles = t.Host.Profiles;
        var (active, _) = await profiles.ActiveAsync();
        Assert.Equal("Built-in simulator", active!.Name);

        var serial = Weighbridge.Application.DefaultProfiles.GenericAscii("COM7") with { Name = "Site indicator" };
        await Assert.ThrowsAsync<Weighbridge.Core.Security.PermissionDeniedException>(() => profiles.SaveAsync(t.Operator, serial, activate: true));
        var bad = serial with { Connection = serial.Connection with { Serial = serial.Connection.Serial with { BaudRate = 5 } } };
        await Assert.ThrowsAsync<ArgumentException>(() => profiles.SaveAsync(t.Admin, bad, activate: false));

        var saved = await profiles.SaveAsync(t.Admin, serial, activate: true, reason: "New indicator installed");
        Assert.Equal(1, saved.Version);
        Assert.Equal("Site indicator", (await profiles.ActiveAsync()).Profile!.Name);
        var changed = await profiles.SaveAsync(t.Admin, saved with { Notes = "baud checked" }, activate: false);
        Assert.Equal(2, changed.Version);
        var list = await profiles.ProfilesAsync();
        Assert.Contains(list, p => p.Name == "Site indicator" && p.InUse && p.Version == 2);

        // A file exported from another PC is never trusted as verified, and an older copy does not overwrite the stored one.
        var imported = await profiles.ImportAsync(t.Admin, (saved with { Verified = true }).ToJson());
        Assert.False(imported.Verified);
        Assert.NotEqual(saved.Id, imported.Id);
        Assert.EndsWith("(imported)", imported.Name);

        await profiles.UpdateWeighbridgeAsync(t.Admin, "Main bridge", 100_000, 400, 20);
        Assert.Equal(400m, (await t.Host.Weighing.LimitsAsync()).MinimumKg);
        var audit = await t.Host.Audit.QueryAsync(t.Admin, new Weighbridge.Data.Stores.AuditFilter { Category = "Devices", PageSize = 50 });
        Assert.Contains(audit.Items, e => e.Action == "ProfileActivated" && e.Reason == "New indicator installed");
        Assert.True((await t.Host.Audit.VerifyAsync()).Valid);
    }
}
