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
    private static readonly Lock LogLock = new();

    public static async Task<int> RunAsync(CommandLine options)
    {
        var output = Directory.CreateDirectory(options.Screenshots!).FullName;
        var logPath = Path.Combine(output, "screenshots.txt");
        File.WriteAllText(logPath, $"Screenshot run started {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}{Environment.NewLine}");
        void Note(string line)
        {
            lock (LogLock) File.AppendAllText(logPath, line + Environment.NewLine);
        }

        // The log is written as the run progresses and every exception lands in it, so a failure on a build agent
        // without a desktop session can be diagnosed from the CI output alone.
        var uiErrors = 0;
        System.Windows.Application.Current.DispatcherUnhandledException += (_, e) =>
        {
            Interlocked.Increment(ref uiErrors);
            Note("UI EXCEPTION: " + e.Exception);
            e.Handled = true;
        };
        Dialogs.Unattended = (kind, message) =>
        {
            if (kind == MessageBoxImage.Error) Interlocked.Increment(ref uiErrors);
            Note($"DIALOG ({kind}): {message}");
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Note("UNHANDLED EXCEPTION: " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Note("UNOBSERVED TASK EXCEPTION: " + e.Exception);
            e.SetObserved();
        };

        var dir = Directory.CreateTempSubdirectory("awms-screens-");
        var loggers = Logging.Create(Path.Combine(output, "logs"));
        App.Log = loggers.CreateLogger("AWMS");
        var step = "start-up";
        try
        {
            step = "create host";
            App.Host = await AppHost.CreateAsync(new AppHostOptions { Paths = new AppPaths(dir.FullName), Logging = loggers });
            var host = App.Host;
            step = "initialise demonstration data";
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
            step = "start simulated devices";
            await host.StartDevicesAsync(admin);
            await App.ApplyAppearanceAsync();
            Note("Host ready (simulated devices, demonstration data).");

            step = "login window";
            var login = new LoginWindow();
            login.Show();
            await Shot(login, Path.Combine(output, "01-login.png"), Note);
            login.Close();

            step = "main window";
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
                step = "page " + page;
                main.Navigate(page);
                await Shot(main, Path.Combine(output, $"{n++:00}-{page}.png"), Note, page == "monitor" ? 3000 : 1500);
            }
            foreach (var (code, name) in new[] { ("si", "sinhala"), ("ta", "tamil") })
            {
                step = "language " + name;
                Loc.Apply(code);
                main.Navigate("history");
                main.Navigate("weighing");
                await Shot(main, Path.Combine(output, $"{n++:00}-weighing-{name}.png"), Note);
            }
            Loc.Apply("en");
            step = "dark theme";
            ThemeManager.Apply("Dark");
            await Shot(main, Path.Combine(output, $"{n++:00}-weighing-dark.png"), Note);
            ThemeManager.Apply("Light");
            step = "transaction window";
            var recent = await host.History.RecentAsync(1);
            if (recent.Count > 0)
            {
                var detail = new TransactionWindow(recent[0].Id);
                detail.Show();
                await Shot(detail, Path.Combine(output, $"{n++:00}-transaction.png"), Note);
                detail.Close();
            }
            step = "shut down";
            main.Hide();
            await host.DisposeAsync();
            App.Host = null!; // already disposed; App.OnExit must not dispose it again
            if (uiErrors > 0)
            {
                Note($"FAILED: {uiErrors} UI error(s), see above.");
                return 1;
            }
            Note("Completed.");
            return 0;
        }
        catch (Exception e)
        {
            Note($"FAILED during '{step}': {e}");
            return 1;
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { dir.Delete(true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
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

    private static async Task Shot(Window window, string path, Action<string> note, int settleMs = 1500)
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
        note($"{Path.GetFileName(path)} {width}x{height}");
    }

    private static async Task Wait(Func<bool> condition, int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(100);
    }
}
