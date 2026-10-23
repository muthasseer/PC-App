namespace Weighbridge.Desktop.Startup;

/// <summary>Command-line options. Without options the interactive application starts.</summary>
public sealed record CommandLine
{
    public const string HelpText = """
        AdvancedWeighbridge.exe [options]

          (no options)          Start the application.
          --data-dir <folder>   Use another data folder (default C:\ProgramData\AdvancedWeighbridge, or AWMS_DATA_DIR).
          --init                Create the data folders and the database, then exit (used by the installer).
          --list-ports          Write the serial ports found on this PC (with USB vendor details) and exit.
          --selftest            Run the built-in self-test with the simulator in a temporary folder and exit (0 = passed).
          --screenshots <dir>   Render the main screens with demonstration data to PNG files and exit.
          --out <file>          Also write the output of --list-ports / --selftest to this file.
          --help                Show this text.
        """;

    public string? DataDirectory { get; init; }
    public bool InitOnly { get; init; }
    public bool ListPorts { get; init; }
    public bool SelfTest { get; init; }
    public string? Screenshots { get; init; }
    public string? OutputFile { get; init; }
    public bool ShowHelp { get; init; }

    public bool Headless => SelfTest || ListPorts || InitOnly || ShowHelp;

    public static CommandLine Parse(IReadOnlyList<string> args)
    {
        var o = new CommandLine();
        for (var i = 0; i < args.Count; i++)
        {
            string Next() => i + 1 < args.Count ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
            switch (args[i].ToLowerInvariant())
            {
                case "--data-dir": o = o with { DataDirectory = Next() }; break;
                case "--init": o = o with { InitOnly = true }; break;
                case "--list-ports": o = o with { ListPorts = true }; break;
                case "--selftest": o = o with { SelfTest = true }; break;
                case "--screenshots": o = o with { Screenshots = Next() }; break;
                case "--out": o = o with { OutputFile = Next() }; break;
                case "--help" or "-h" or "/?": o = o with { ShowHelp = true }; break;
                default: throw new ArgumentException($"Unknown option '{args[i]}'. Use --help.");
            }
        }
        return o;
    }
}
