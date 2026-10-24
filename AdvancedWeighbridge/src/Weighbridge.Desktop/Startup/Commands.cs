using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Weighbridge.Application;
using Weighbridge.Devices.Discovery;

namespace Weighbridge.Desktop.Startup;

/// <summary>Non-interactive commands used by the installer and by support staff.</summary>
internal static partial class Commands
{
    public static string Version => typeof(Commands).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public static int ListPorts(CommandLine options)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Advanced Weighbridge {Version} – serial ports on {Environment.MachineName} ({DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz})");
        var ports = PortDiscovery.List();
        if (ports.Count == 0) sb.AppendLine("No serial (COM) ports were found. Connect the USB-serial adapter or install its driver from the adapter manufacturer.");
        foreach (var p in ports)
        {
            sb.Append(p.Display);
            if (p.ChipVendor is { Length: > 0 } chip) sb.Append($"  [USB chip: {chip}]");
            if (p.DriverProblem) sb.Append($"  [DRIVER PROBLEM, code {p.ConfigManagerErrorCode}{(p.DriverHelpUrl is { } url ? $" – driver from the manufacturer: {url}" : string.Empty)}]");
            sb.AppendLine();
        }
        Write(sb.ToString(), options.OutputFile);
        return 0;
    }

    public static async Task<int> InitializeDataAsync(AppPaths paths, ILoggerFactory loggers)
    {
        await using var host = await AppHost.CreateAsync(new AppHostOptions { Paths = paths, Logging = loggers });
        var initialized = await host.Setup.IsInitializedAsync();
        loggers.CreateLogger("AWMS").LogInformation("Data folder {Root} ready (database {State})", paths.Root, initialized ? "already set up" : "created; first-run set-up pending");
        return 0;
    }

    /// <summary>Writes to the parent console (when started from a command prompt) and optionally to a file.</summary>
    public static void Write(string text, string? file)
    {
        if (AttachConsole(-1))
        {
            using var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            stdout.WriteLine();
            stdout.Write(text);
        }
        if (!string.IsNullOrWhiteSpace(file))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
            File.WriteAllText(file, text, Encoding.UTF8);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);
}
