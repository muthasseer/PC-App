namespace Weighbridge.Application;

/// <summary>
/// Where the application keeps its data. Default on Windows: <c>C:\ProgramData\AdvancedWeighbridge</c> (shared by all
/// Windows users of the PC, preserved on upgrade and uninstall). Override with the AWMS_DATA_DIR environment variable.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string root)
    {
        Root = Path.GetFullPath(root);
    }

    public static AppPaths Default()
    {
        var overridden = Environment.GetEnvironmentVariable("AWMS_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overridden)) return new AppPaths(overridden);
        var baseDir = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ".local", "share");
        return new AppPaths(Path.Combine(baseDir, "AdvancedWeighbridge"));
    }

    public string Root { get; }
    public string DatabaseFile => Path.Combine(Root, "data", "weighbridge.db");
    public string EvidenceDirectory => Path.Combine(Root, "evidence");
    public string BackupDirectory => Path.Combine(Root, "backups");
    public string LogDirectory => Path.Combine(Root, "logs");
    public string CaptureDirectory => Path.Combine(Root, "captures");
    public string ExportDirectory => Path.Combine(Root, "exports");
    public string ProfileDirectory => Path.Combine(Root, "profiles");
    public string KeyDirectory => Path.Combine(Root, "keys");
    public string RunMarkerFile => Path.Combine(Root, "data", "running.marker");

    /// <summary>Plug-ins live next to the program (Program Files – administrator-writable only).</summary>
    public string PluginDirectory => Path.Combine(AppContext.BaseDirectory, "plugins");

    public void EnsureCreated()
    {
        foreach (var dir in new[] { Path.GetDirectoryName(DatabaseFile)!, EvidenceDirectory, BackupDirectory, LogDirectory, CaptureDirectory, ExportDirectory, ProfileDirectory, KeyDirectory })
        {
            Directory.CreateDirectory(dir);
        }
    }
}
