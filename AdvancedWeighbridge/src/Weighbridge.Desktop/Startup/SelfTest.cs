using System.Diagnostics;
using System.Text;
using Weighbridge.Application;
using Weighbridge.Application.Security;
using Weighbridge.Application.Settings;
using Weighbridge.Application.Weighing;
using Weighbridge.Core.Indicators;
using Weighbridge.Core.Security;
using Weighbridge.Core.Weighing;
using Weighbridge.Data;
using Weighbridge.Devices.Discovery;
using Weighbridge.Desktop.Infrastructure;
using Weighbridge.Reporting.Tickets;

namespace Weighbridge.Desktop.Startup;

/// <summary>
/// End-to-end check of the installed application on this PC, without hardware: database (SQLite), Windows data
/// protection, serial port enumeration, printing subsystem, the simulator-driven weighing workflow, ticket rendering,
/// backup and the audit chain. Runs in a temporary folder and never touches the real data.
/// </summary>
internal static class SelfTest
{
    public static async Task<int> RunAsync(CommandLine options)
    {
        var report = new StringBuilder();
        var failures = 0;
        var watch = Stopwatch.StartNew();
        void Log(bool ok, string step, string detail)
        {
            if (!ok) failures++;
            report.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {step}: {detail}");
        }
        async Task Step(string name, Func<Task<string>> action)
        {
            try { Log(true, name, await action()); }
            catch (Exception e) { Log(false, name, $"{e.GetType().Name}: {e.Message}"); }
        }

        report.AppendLine($"Advanced Weighbridge {Commands.Version} self-test on {Environment.MachineName}, {Environment.OSVersion}, {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        var dir = Directory.CreateTempSubdirectory("awms-selftest-");
        AppHost? host = null;
        try
        {
            await Step("Create database", async () =>
            {
                host = await AppHost.CreateAsync(new AppHostOptions { Paths = new AppPaths(dir.FullName) });
                return $"SQLite database created in {dir.FullName}";
            });
            if (host is null) throw new InvalidOperationException("The application host could not be created.");
            var h = host;
            UserContext admin = null!;
            await Step("First-run set-up", async () =>
            {
                await h.Setup.InitializeAsync(new SetupRequest
                {
                    Company = new CompanySettings { Name = "Self-test company" }, AdminUsername = "admin", AdminDisplayName = "Administrator", AdminPassword = "SelfTest#2026",
                    SiteCode = "TEST", WeighbridgeCode = "WB1", CapacityKg = 80000, MinimumCaptureKg = 200,
                });
                admin = (await h.Auth.LoginAsync("admin", "SelfTest#2026")).User ?? throw new InvalidOperationException("Sign-in failed.");
                return "administrator created and signed in";
            });
            await Step("Secret protection", () =>
            {
                var p = h.Settings.Protector;
                return Task.FromResult(p.Unprotect(p.Protect("secret-ü-ශ්‍රී")) == "secret-ü-ශ්‍රී" ? $"{p.GetType().Name} round trip OK" : throw new InvalidOperationException("Round trip mismatch."));
            });
            await Step("Serial ports", () => Task.FromResult($"{PortDiscovery.List().Count} port(s) found: {string.Join(", ", PortDiscovery.List().Select(p => p.PortName))}"));
            await Step("Printers", () => Task.FromResult($"{WpfPrintService.Printers().Count} printer(s) installed"));

            var materialId = 0L;
            await Step("Master data", async () =>
            {
                materialId = await h.MasterData.SaveMaterialAsync(admin, new MaterialEntity { Code = "SAND", Name = "River sand" });
                return "material created";
            });
            await Step("Start devices (simulator)", async () =>
            {
                await h.StartDevicesAsync(admin);
                await WaitAsync(() => h.Indicator.Snapshot.Connection == ConnectionState.Connected, 15);
                return $"indicator connected: {h.Indicator.Snapshot.PortDescription}";
            });

            var truck = h.Simulator.Fleet[0];
            CaptureResult? first = null, second = null;
            await Step("First weighing (loaded)", async () =>
            {
                h.Simulator.DriveOn(truck, loaded: true);
                first = await CaptureWhenStableAsync(h, admin, truck.Registration, materialId);
                return $"ticket {first.Transaction!.TicketNo}, first weight {first.Transaction.First}";
            });
            await Step("Second weighing (empty)", async () =>
            {
                h.Simulator.DriveOff();
                await WaitAsync(() => h.Indicator.Snapshot.Latest?.Value is < 100m, 20);
                h.Simulator.DriveOn(truck, loaded: false);
                second = await CaptureWhenStableAsync(h, admin, truck.Registration, materialId);
                if (!second.IsFinal) throw new InvalidOperationException("The second weighing did not complete the ticket.");
                var net = second.Transaction!.Net!.Value.Kilograms;
                if (Math.Abs(net - truck.LoadKg) > 60) throw new InvalidOperationException($"Net {net} kg differs from the simulated load {truck.LoadKg} kg.");
                return $"completed, net {net:0} kg (simulated load {truck.LoadKg:0} kg)";
            });
            await Step("Ticket PDF and print raster", async () =>
            {
                var pdf = await h.Tickets.PdfAsync(second!.Transaction!.Id);
                if (pdf.Length < 1000 || pdf[0] != (byte)'%') throw new InvalidOperationException("Invalid PDF.");
                var data = await h.Tickets.BuildAsync(second.Transaction.Id);
                using var bitmap = TicketRenderer.RenderBitmap(data, await h.Tickets.TemplateAsync(), 203);
                var image = WpfPrintService.ToBitmapSource(bitmap);
                return $"PDF {pdf.Length / 1024} KB, raster {image.PixelWidth}×{image.PixelHeight} px";
            });
            await Step("Manual weight is labelled", async () =>
            {
                var r = await h.Weighing.ManualEntryAsync(admin, new ManualEntryRequest
                {
                    Details = new TransactionDetails { VehicleReg = "MANUAL-1", MaterialId = materialId }, WeightKg = 12345, Reason = "Self-test manual entry",
                    Approval = new SupervisorApproval(admin.UserId, admin.Username, admin.DisplayName, h.Time.GetUtcNow()),
                });
                if (!r.Success || !r.Transaction!.HasManualWeight) throw new InvalidOperationException(r.Message);
                return "manual weight recorded and flagged";
            });
            await Step("Backup", async () =>
            {
                var b = await h.Backups.BackupNowAsync(admin);
                var v = await h.Backups.VerifyAsync(b.Path);
                return $"{Path.GetFileName(b.Path)} verified ({v.Manifest.Transactions} transactions)";
            });
            await Step("Audit chain", async () =>
            {
                var v = await h.Audit.VerifyAsync();
                return v.Valid ? $"intact ({v.Checked} events)" : throw new InvalidOperationException(v.Message);
            });
        }
        catch (Exception e)
        {
            Log(false, "Self-test", e.Message);
        }
        finally
        {
            if (host is not null) await host.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { dir.Delete(true); }
            catch (IOException) { }
        }

        report.AppendLine(failures == 0 ? $"RESULT: PASSED in {watch.Elapsed.TotalSeconds:0.0} s" : $"RESULT: FAILED ({failures} step(s)) in {watch.Elapsed.TotalSeconds:0.0} s");
        Commands.Write(report.ToString(), options.OutputFile);
        return failures == 0 ? 0 : 1;
    }

    private static async Task<CaptureResult> CaptureWhenStableAsync(AppHost h, UserContext user, string vehicle, long materialId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        CaptureResult? last = null;
        while (DateTime.UtcNow < deadline)
        {
            var snap = h.Indicator.Snapshot;
            if (snap.Stability.IsStable && snap.Latest?.Value is > 1000m)
            {
                last = await h.Weighing.CaptureAsync(user, new CaptureRequest
                {
                    Details = new TransactionDetails { VehicleReg = vehicle, MaterialId = materialId }, Mode = OperatingMode.SemiAutomatic, Snapshot = snap,
                    EvidenceFrames = h.Indicator.Current?.EvidenceFor(snap.Latest.Sequence, 5) ?? [],
                });
                if (last.Success) return last;
                if (last.Failure is not (CaptureFailure.WaitForStability or CaptureFailure.Invalid)) throw new InvalidOperationException(last.Message);
            }
            await Task.Delay(200);
        }
        throw new TimeoutException($"No stable weight within 30 s{(last is null ? string.Empty : $": {last.Message}")}");
    }

    private static async Task WaitAsync(Func<bool> condition, int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Condition not met within {seconds} s.");
            await Task.Delay(100);
        }
    }
}
