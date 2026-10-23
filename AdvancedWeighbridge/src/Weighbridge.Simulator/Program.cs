using System.Globalization;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using Weighbridge.Core.Common;
using Weighbridge.Core.Indicators;
using Weighbridge.Devices.Simulation;
using Weighbridge.Devices.Transports;

// awms-indicator-simulator – emulates a weighbridge indicator for testing the application without hardware.
//
//   awms-indicator-simulator --port COM11 [--baud 9600] [--data 8] [--parity None] [--stop One]
//   awms-indicator-simulator --tcp 4001
//   options: --template "{status},{mode},{weight:+000000.0}kg\r\n" | @toledo | @toledo+cs
//            --interval 100  --noise 5  --division 10  --replay capture.wbcap [--loop]  --auto  --seconds N
//
// With a virtual null-modem pair (e.g. com0com COM10<->COM11, or socat on Linux) point the application at one end
// and the simulator at the other. Keys: L = loaded truck on, E = empty truck on, O = drive off, A = automatic traffic,
// V = overload, X = garbage bytes, D = pause output (disconnect), Q = quit.

var opts = Options.Parse(args);
if (opts.Help)
{
    Console.WriteLine(Options.HelpText);
    return 0;
}

var settings = new SimulatorSettings { Template = opts.Template, IntervalMs = opts.IntervalMs, NoiseKg = opts.NoiseKg, DivisionKg = opts.DivisionKg, AutoCycle = opts.Auto };
var world = new SimulatorWorld(settings) { AutoCycle = opts.Auto };
var output = await Output.OpenAsync(opts);
Console.WriteLine($"Simulator sending to {output.Description}. Template: {opts.Template}. Press H for keys, Q to quit.");

using var cts = new CancellationTokenSource();
if (opts.Seconds is { } limit) cts.CancelAfter(TimeSpan.FromSeconds(limit));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var paused = false;
var garbage = false;

var keys = Task.Run(() =>
{
    while (!cts.IsCancellationRequested)
    {
        if (Console.IsInputRedirected || !Console.KeyAvailable) { Thread.Sleep(50); continue; }
        switch (char.ToUpperInvariant(Console.ReadKey(intercept: true).KeyChar))
        {
            case 'L': world.DriveOn(world.Fleet[Random.Shared.Next(world.Fleet.Count)], loaded: true); Console.WriteLine("Loaded truck driving on."); break;
            case 'E': world.DriveOn(world.Fleet[Random.Shared.Next(world.Fleet.Count)], loaded: false); Console.WriteLine("Empty truck driving on."); break;
            case 'O': world.DriveOff(); Console.WriteLine("Driving off."); break;
            case 'A': world.AutoCycle = !world.AutoCycle; Console.WriteLine($"Automatic traffic {(world.AutoCycle ? "on" : "off")}."); break;
            case 'V': world.ForceOverload = !world.ForceOverload; Console.WriteLine($"Overload {(world.ForceOverload ? "on" : "off")}."); break;
            case 'X': garbage = true; Console.WriteLine("Sending garbage bytes once."); break;
            case 'D': paused = !paused; Console.WriteLine(paused ? "Output paused (simulated disconnect)." : "Output resumed."); break;
            case 'H': Console.WriteLine("L loaded on · E empty on · O off · A auto traffic · V overload · X garbage · D pause · Q quit"); break;
            case 'Q': cts.Cancel(); break;
        }
    }
});

try
{
    if (opts.ReplayFile is { } replay)
    {
        var (_, records) = RawCaptureFormat.Load(replay);
        do
        {
            var start = DateTime.UtcNow;
            foreach (var (offset, data) in records)
            {
                var wait = start.AddMilliseconds(offset) - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, cts.Token);
                if (!paused) await output.WriteAsync(data, cts.Token);
            }
        }
        while (opts.Loop && !cts.IsCancellationRequested);
    }
    else
    {
        var last = DateTime.MinValue;
        while (!cts.IsCancellationRequested)
        {
            var reading = world.Read();
            if (!paused)
            {
                await output.WriteAsync(SimulatorWorld.Format(opts.Template, reading), cts.Token);
                if (garbage)
                {
                    garbage = false;
                    await output.WriteAsync([0xFF, 0x13, 0x00, 0x7F, (byte)'#', (byte)'?', 0x0D, 0x0A], cts.Token);
                }
            }
            if ((DateTime.UtcNow - last).TotalSeconds >= 1)
            {
                last = DateTime.UtcNow;
                Console.Write($"\r{world.Describe(),-50} {reading.Kg,10:0} kg {(reading.Moving ? "MOTION" : "stable"),-7}{(paused ? " PAUSED" : string.Empty)}   ");
            }
            await Task.Delay(opts.IntervalMs, cts.Token);
        }
    }
}
catch (OperationCanceledException) { }
finally
{
    await output.DisposeAsync();
    Console.WriteLine();
}
await keys;
return 0;

internal sealed record Options
{
    public const string HelpText = """
        awms-indicator-simulator – emulates a weighbridge indicator.

          --port <COMx|/dev/ttyX>   serial port to send to
          --baud <n> --data <7|8> --parity <None|Odd|Even> --stop <One|Two>
          --tcp <port>              listen on TCP and send to every connected client
          --template <text>         output format with {status} {mode} {weight:+000000.0} {unit} {sign}, or @toledo / @toledo+cs
          --interval <ms>           time between messages (default 100)
          --noise <kg> --division <kg>
          --auto                    trucks come and go automatically
          --replay <file.wbcap> [--loop]   replay a raw capture instead of simulating
          --seconds <n>             stop after n seconds (for scripts)
        """;

    public string? Port { get; init; }
    public int Baud { get; init; } = 9600;
    public int DataBits { get; init; } = 8;
    public Parity Parity { get; init; } = Parity.None;
    public StopBits StopBits { get; init; } = StopBits.One;
    public int? TcpPort { get; init; }
    public string Template { get; init; } = "{status},{mode},{weight:+000000.0}kg\\r\\n";
    public int IntervalMs { get; init; } = 100;
    public decimal NoiseKg { get; init; } = 5m;
    public decimal DivisionKg { get; init; } = 10m;
    public bool Auto { get; init; }
    public string? ReplayFile { get; init; }
    public bool Loop { get; init; }
    public int? Seconds { get; init; }
    public bool Help { get; init; }

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            o = args[i] switch
            {
                "--port" => o with { Port = Next() },
                "--baud" => o with { Baud = int.Parse(Next(), CultureInfo.InvariantCulture) },
                "--data" => o with { DataBits = int.Parse(Next(), CultureInfo.InvariantCulture) },
                "--parity" => o with { Parity = Enum.Parse<Parity>(Next(), true) },
                "--stop" => o with { StopBits = Enum.Parse<StopBits>(Next(), true) },
                "--tcp" => o with { TcpPort = int.Parse(Next(), CultureInfo.InvariantCulture) },
                "--template" => o with { Template = Next() },
                "--interval" => o with { IntervalMs = int.Parse(Next(), CultureInfo.InvariantCulture) },
                "--noise" => o with { NoiseKg = decimal.Parse(Next(), CultureInfo.InvariantCulture) },
                "--division" => o with { DivisionKg = decimal.Parse(Next(), CultureInfo.InvariantCulture) },
                "--auto" => o with { Auto = true },
                "--replay" => o with { ReplayFile = Next() },
                "--loop" => o with { Loop = true },
                "--seconds" => o with { Seconds = int.Parse(Next(), CultureInfo.InvariantCulture) },
                "--help" or "-h" => o with { Help = true },
                _ => throw new ArgumentException($"Unknown option {args[i]}"),
            };
        }
        if (!o.Help && o.Port is null && o.TcpPort is null) o = o with { Help = true };
        return o;
    }
}

/// <summary>Where the simulated output goes: a serial port, or all clients connected to a TCP port.</summary>
internal abstract class Output : IAsyncDisposable
{
    public abstract string Description { get; }

    public abstract Task WriteAsync(byte[] data, CancellationToken ct);

    public abstract ValueTask DisposeAsync();

    public static Task<Output> OpenAsync(Options o) => Task.FromResult<Output>(o.TcpPort is { } tcp ? new TcpOutput(tcp) : new SerialOutput(o));

    private sealed class SerialOutput : Output
    {
        private readonly SerialPort _port;

        public SerialOutput(Options o)
        {
            _port = new SerialPort(o.Port!, o.Baud, o.Parity, o.DataBits, o.StopBits) { WriteTimeout = 2000 };
            _port.Open();
        }

        public override string Description => $"{_port.PortName} {_port.BaudRate} {_port.DataBits}{_port.Parity.ToString()[0]}{(_port.StopBits == StopBits.Two ? 2 : 1)}";

        public override Task WriteAsync(byte[] data, CancellationToken ct)
        {
            _port.Write(data, 0, data.Length);
            return Task.CompletedTask;
        }

        public override ValueTask DisposeAsync()
        {
            _port.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TcpOutput : Output
    {
        private readonly TcpListener _listener;
        private readonly List<TcpClient> _clients = [];
        private readonly int _port;

        public TcpOutput(int port)
        {
            _port = port;
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            _ = AcceptAsync();
        }

        public override string Description => $"TCP port {_port} (connect the application to this PC's address)";

        private async Task AcceptAsync()
        {
            while (true)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    lock (_clients) _clients.Add(client);
                    Console.WriteLine($"\nClient connected: {client.Client.RemoteEndPoint}");
                }
                catch (Exception e) when (e is ObjectDisposedException or SocketException)
                {
                    return;
                }
            }
        }

        public override async Task WriteAsync(byte[] data, CancellationToken ct)
        {
            TcpClient[] clients;
            lock (_clients) clients = [.. _clients];
            foreach (var c in clients)
            {
                try { await c.GetStream().WriteAsync(data, ct); }
                catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
                {
                    lock (_clients) _clients.Remove(c);
                    c.Dispose();
                }
            }
        }

        public override ValueTask DisposeAsync()
        {
            _listener.Stop();
            lock (_clients) foreach (var c in _clients) c.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
