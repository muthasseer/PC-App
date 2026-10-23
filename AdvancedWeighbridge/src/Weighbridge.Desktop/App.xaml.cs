using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;
using Weighbridge.Application;
using Weighbridge.Application.Settings;
using Weighbridge.Core.Security;
using Weighbridge.Desktop.Infrastructure;
using Weighbridge.Desktop.Startup;
using Weighbridge.Desktop.Windows;

namespace Weighbridge.Desktop;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;
    private ILoggerFactory? _loggers;

    public static AppHost Host { get; internal set; } = null!;
    public static CommandLine Options { get; private set; } = new();
    public static Microsoft.Extensions.Logging.ILogger Log { get; private set; } = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    /// <summary>Raised when an operator signs in or out (the main window rebuilds its navigation).</summary>
    public static event EventHandler<UserContext?>? UserChanged;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Options = CommandLine.Parse(e.Args);
        try
        {
            var exit = await RunAsync();
            if (exit is { } code) Shutdown(code);
        }
        catch (Exception ex)
        {
            Log.LogCritical(ex, "Start-up failed");
            if (!Options.Headless) MessageBox.Show($"The application could not start:\n\n{ex.Message}\n\nDetails are in the log folder.", "Advanced Weighbridge", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>Returns an exit code for command-line modes, or null when the interactive application keeps running.</summary>
    private async Task<int?> RunAsync()
    {
        if (Options.ShowHelp)
        {
            Console.WriteLine(CommandLine.HelpText);
            return 0;
        }
        if (Options.ListPorts) return Commands.ListPorts(Options);
        if (Options.SelfTest) return await SelfTest.RunAsync(Options);
        if (Options.Screenshots is not null) return await ScreenshotRunner.RunAsync(Options);

        var paths = Options.DataDirectory is { } dir ? new AppPaths(dir) : AppPaths.Default();
        paths.EnsureCreated();
        _loggers = Logging.Create(paths.LogDirectory);
        Log = _loggers.CreateLogger("AWMS");
        Log.LogInformation("Advanced Weighbridge {Version} starting; data folder {Root}", Commands.Version, paths.Root);
        if (Options.InitOnly) return await Commands.InitializeDataAsync(paths, _loggers);

        _singleInstance = new Mutex(true, @"Global\AdvancedWeighbridge.Desktop", out var first);
        if (!first)
        {
            MessageBox.Show("Advanced Weighbridge is already running on this PC. Only one copy can use the indicator at a time.", "Advanced Weighbridge",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return 0;
        }

        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.LogCritical(args.ExceptionObject as Exception, "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.LogError(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        Host = await AppHost.CreateAsync(new AppHostOptions { Paths = paths, Logging = _loggers });
        await ApplyAppearanceAsync();

        if (!await Host.Setup.IsInitializedAsync())
        {
            var setup = new FirstRunWindow();
            if (setup.ShowDialog() != true) return 0;
            await ApplyAppearanceAsync();
        }

        var user = SignIn();
        if (user is null) return 0;
        await Host.StartDevicesAsync(user);
        var main = new MainWindow();
        MainWindow = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        main.Show();
        UserChanged?.Invoke(this, user);
        return null;
    }

    /// <summary>Shows the sign-in window; returns the signed-in user or null when cancelled.</summary>
    public static UserContext? SignIn()
    {
        var login = new LoginWindow();
        if (login.ShowDialog() != true || login.User is null) return null;
        Host.Session.SignIn(login.User);
        _ = Host.Settings.GetAsync<SecuritySettings>(SettingKeys.Security).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully) Host.Session.TimeoutMinutes = t.Result.SessionTimeoutMinutes;
        }, TaskScheduler.Default);
        UserChanged?.Invoke(null, login.User);
        return login.User;
    }

    public static void SignOut()
    {
        Host.Session.SignOut();
        UserChanged?.Invoke(null, null);
    }

    public static async Task ApplyAppearanceAsync()
    {
        var loc = await Host.Settings.GetAsync<LocalizationSettings>(SettingKeys.Localization);
        Loc.Apply(loc.Language);
        ThemeManager.Apply(loc.Theme);
        GramsConverter.Display = loc.Display;
    }

    private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.LogError(e.Exception, "Unhandled UI exception");
        if (UiErrors.IsUserFacing(e.Exception))
        {
            Dialogs.Error(e.Exception.Message);
            e.Handled = true;
            return;
        }
        Dialogs.Error($"An unexpected error occurred: {e.Exception.Message}\n\nThe details were written to the log. The application keeps running; recorded weighings are not affected.");
        e.Handled = true;
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            if (Host is not null) await Host.DisposeAsync();
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Shutdown error");
        }
        Log.LogInformation("Advanced Weighbridge stopped (exit code {Code})", e.ApplicationExitCode);
        _loggers?.Dispose();
        await Serilog.Log.CloseAndFlushAsync();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}

internal static class Logging
{
    /// <summary>Rolling daily log files, kept for 60 days (only log files are pruned, never business records).</summary>
    public static ILoggerFactory Create(string folder)
    {
        Directory.CreateDirectory(folder);
        Serilog.Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.WithProperty("Machine", Environment.MachineName)
            .WriteTo.File(Path.Combine(folder, "awms-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 60,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        return new SerilogLoggerFactory(Serilog.Log.Logger, dispose: false);
    }
}
