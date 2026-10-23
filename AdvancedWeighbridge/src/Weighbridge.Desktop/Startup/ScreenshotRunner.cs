using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Weighbridge.Application;
using Weighbridge.Application.Settings;
using Weighbridge.Application.Weighing;
using Weighbridge.Core.Indicators;
using Weighbridge.Core.Indicators.Framing;
using Weighbridge.Core.Indicators.Stability;
using Weighbridge.Core.Security;
using Weighbridge.Core.Weighing;
using Weighbridge.Data;
using Weighbridge.Devices.IO;
using Weighbridge.Desktop.Infrastructure;
using Weighbridge.Desktop.Windows;

namespace Weighbridge.Desktop.Startup;

/// <summary>
/// Renders the main screens with demonstration data (simulator, temporary folder) to PNG files. Used by the Windows
/// CI build to show that the application starts and every page loads, and for the user manual.
/// </summary>
internal static class ScreenshotRunner
{
    public static async Task<int> RunAsync(CommandLine options)
    {
        var output = Directory.CreateDirectory(options.Screenshots!).FullName;
        var dir = Directory.CreateTempSubdirectory("awms-screens-");
        var log = new List<string>();
        try
        {
            App.Host = await AppHost.CreateAsync(new AppHostOptions { Paths = new AppPaths(dir.FullName) });
            var host = App.Host;
            await host.Setup.InitializeAsync(new SetupRequest
            {
                Company = new CompanySettings { Name = "Kegalle Metal Quarry (Pvt) Ltd", Address = "Colombo Road, Kegalle" },
                AdminUsername = "admin", AdminDisplayName = "Nimal Perera", AdminPassword = "Screens#2026", SiteCode = "KGL", SiteName = "Kegalle quarry",
                WeighbridgeCode = "WB1", WeighbridgeName = "Main weighbridge", CapacityKg = 80000, MinimumCaptureKg = 200,
            });
            var admin = (await host.Auth.LoginAsync("admin", "Screens#2026")).User!;
            await host.Settings.SaveAsync(SettingKeys.Hardware, HardwareConfig.CreateSimulated(), admin);
            await SeedAsync(host, admin);
            host.Session.SignIn(admin);
            await host.StartDevicesAsync(admin);
            await App.ApplyAppearanceAsync();

            var login = new LoginWindow();
            login.Show();
            await Shot(login, Path.Combine(output, "01-login.png"), log);
            login.Close();

            var main = new MainWindow { WindowState = WindowState.Normal, Width = 1600, Height = 900, Left = 0, Top = 0 };
            System.Windows.Application.Current.MainWindow = main;
            main.Show();
            var world = host.Simulator;
            world.DriveOn(world.Fleet[1], loaded: true);
            await Wait(() => host.Indicator.Snapshot.Stability.IsStable && host.Indicator.Snapshot.Latest?.Value > 1000, 30);
            var pages = new[] { "weighing", "history", "reports", "master", "wizard", "monitor", "hardware", "users", "settings", "backup", "audit", "notifications", "status" };
            var n = 2;
            foreach (var page in pages)
            {
                main.Navigate(page);
                await Shot(main, Path.Combine(output, $"{n++:00}-{page}.png"), log, page == "monitor" ? 3000 : 1500);
            }
            foreach (var (code, name) in new[] { ("si", "sinhala"), ("ta", "tamil") })
            {
                Loc.Apply(code);
                main.Navigate("history");
                main.Navigate("weighing");
                await Shot(main, Path.Combine(output, $"{n++:00}-weighing-{name}.png"), log);
            }
            Loc.Apply("en");
            ThemeManager.Apply("Dark");
            await Shot(main, Path.Combine(output, $"{n++:00}-weighing-dark.png"), log);
            ThemeManager.Apply("Light");
            var recent = await host.History.RecentAsync(1);
            if (recent.Count > 0)
            {
                var detail = new TransactionWindow(recent[0].Id);
                detail.Show();
                await Shot(detail, Path.Combine(output, $"{n++:00}-transaction.png"), log);
                detail.Close();
            }
            main.Hide();
            await host.DisposeAsync();
            App.Host = null!; // already disposed; App.OnExit must not dispose it again
            File.WriteAllLines(Path.Combine(output, "screenshots.txt"), log);
            return 0;
        }
        catch (Exception e)
        {
            log.Add("FAILED: " + e);
            File.WriteAllLines(Path.Combine(output, "screenshots.txt"), log);
            return 1;
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { dir.Delete(true); }
            catch (IOException) { }
        }
    }

    private static async Task SeedAsync(AppHost host, UserContext admin)
    {
        var md = host.MasterData;
        var sand = await md.SaveMaterialAsync(admin, new MaterialEntity { Code = "SAND", Name = "River sand" });
        var metal = await md.SaveMaterialAsync(admin, new MaterialEntity { Code = "M34", Name = "Metal 3/4\"" });
        await md.SaveMaterialAsync(admin, new MaterialEntity { Code = "DUST", Name = "Quarry dust" });
        var c1 = await md.SavePartyAsync(admin, PartyKind.Customer, new PartyEntity { Code = "C001", Name = "Lanka Builders (Pvt) Ltd" });
        var c2 = await md.SavePartyAsync(admin, PartyKind.Customer, new PartyEntity { Code = "C002", Name = "Road Development Authority" });
        await md.SavePartyAsync(admin, PartyKind.Supplier, new PartyEntity { Code = "S001", Name = "Sabaragamuwa Transport" });
        await host.Auth.CreateUserAsync(admin, "kamala", "Kamala Silva", "Operator#2026", Roles.Operator, mustChangePassword: false);
        await host.Auth.CreateUserAsync(admin, "ravi", "Ravi Kumar", "Manager#2026", Roles.Manager, mustChangePassword: false);
        var trips = new (string Vehicle, decimal First, decimal Second, long Material, long Customer)[]
        {
            ("WP CAB-1234", 32460, 12310, sand, c1), ("NC LK-5521", 24050, 9850, metal, c2), ("SP KA-7788", 41480, 15390, metal, c1),
            ("NW 62-0931", 15990, 7600, sand, c2), ("WP LH-4410", 28730, 11020, metal, c1),
        };
        var seq = 1L;
        foreach (var (vehicle, first, second, material, customer) in trips)
        {
            foreach (var kg in new[] { first, second })
            {
                var details = new TransactionDetails { VehicleReg = vehicle, MaterialId = material, CustomerId = customer, Direction = TransactionDirection.Outbound };
                var r = await host.Weighing.CaptureAsync(admin, new CaptureRequest { Details = details, Mode = OperatingMode.SemiAutomatic, Snapshot = Snapshot(host, kg, seq++) });
                if (!r.Success) throw new InvalidOperationException(r.Message);
            }
        }
        await host.Weighing.CaptureAsync(admin, new CaptureRequest
        {
            Details = new TransactionDetails { VehicleReg = "CP DA-3021", MaterialId = sand, CustomerId = c2 }, Mode = OperatingMode.SemiAutomatic, Snapshot = Snapshot(host, 27310, seq),
        });
    }

    private static IndicatorSnapshot Snapshot(AppHost host, decimal kg, long sequence)
    {
        var now = host.Time.GetUtcNow();
        var text = $"ST,GS,+{kg:000000.0}kg";
        return new IndicatorSnapshot
        {
            Connection = ConnectionState.Connected,
            Latest = new WeightReading { Value = kg, Flags = ReadingFlags.Stable | ReadingFlags.Gross, Text = text, ReceivedAt = now.AddMilliseconds(-60), Sequence = sequence, Frame = RawFrame.FromText(text, now, sequence) },
            Stability = new StabilityState(true, StabilityReason.Stable, 0, 2600, 26, kg),
            ProfileName = "Demonstration data", ProfileVersion = 1, PortDescription = "Simulator", Timestamp = now,
        };
    }

    private static async Task Shot(Window window, string path, List<string> log, int settleMs = 1500)
    {
        await Task.Delay(settleMs);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var source = PresentationSource.FromVisual(window);
        var dpi = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        var width = (int)Math.Ceiling(window.ActualWidth * dpi);
        var height = (int)Math.Ceiling(window.ActualHeight * dpi);
        var bitmap = new RenderTargetBitmap(width, height, 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
        if (window.Content is Visual content) bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        await using (var file = File.Create(path)) encoder.Save(file);
        log.Add($"{Path.GetFileName(path)} {width}x{height}");
    }

    private static async Task Wait(Func<bool> condition, int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(100);
    }
}
