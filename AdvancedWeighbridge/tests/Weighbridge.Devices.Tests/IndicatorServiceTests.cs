using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Weighbridge.Core.Indicators;
using Weighbridge.Core.Indicators.Parsing;
using Weighbridge.Core.Indicators.Stability;
using Weighbridge.Devices.Indicator;
using Weighbridge.Devices.Transports;

namespace Weighbridge.Devices.Tests;

public class IndicatorServiceTests
{
    private static IndicatorProfile Profile(TransportKind kind = TransportKind.Serial) => new()
    {
        Name = "Test indicator",
        Connection = new ConnectionSettings { Transport = kind, ReadTimeoutMs = 400, ReconnectIntervalMs = 250 },
        Stability = new StabilityOptions { Mode = StabilityMode.Software, WindowMs = 0, MinSamples = 1 },
    };

    [Fact]
    public void Publishes_parsed_readings_and_counts_rejections()
    {
        var t = new FakeTransport()
            .Enqueue("ST,GS,+012500.0kg\r\nST,GS,+0125")
            .Enqueue("10.0kg\r\n#garbage#?\u0001\r\n");
        using var svc = new IndicatorService(Profile(), new FakeFactory(t));
        var frames = new List<DecodedFrame>();
        svc.FrameDecoded += (_, f) => { lock (frames) frames.Add(f); };
        svc.Start();

        Wait.Until(() => svc.Current.FramesRejected == 1 && svc.Current.FramesParsed == 2, because: "frames decoded");
        var s = svc.Current;
        Assert.Equal(ConnectionState.Connected, s.Connection);
        Assert.Equal(12510m, s.Latest!.Value);
        Assert.Contains("control", s.LastRejectReason);
        Assert.True(s.NewestFrameInvalid);
        Assert.Equal("Test indicator", s.ProfileName);
        lock (frames) Assert.Equal(3, frames.Count);
        Assert.Equal(3, svc.RecentFrames().Count);
        Assert.Equal(2, svc.EvidenceFor(2, 10).Count);
    }

    [Fact]
    public void Goes_silent_without_data()
    {
        var t = new FakeTransport().Enqueue("ST,GS,+012500.0kg\r\n");
        using var svc = new IndicatorService(Profile(), new FakeFactory(t));
        svc.Start();
        Wait.Until(() => svc.Current.Connection == ConnectionState.Connected);
        Wait.Until(() => svc.Current.Connection == ConnectionState.Silent, 3000, "silent after read timeout");
        Assert.NotNull(svc.Current.LastError);
        Assert.Equal(12500m, svc.Current.Latest!.Value); // last reading kept, but it is now stale
    }

    [Fact]
    public void Reconnects_after_disconnect_and_failed_open()
    {
        var first = new FakeTransport().Enqueue("ST,GS,+001000.0kg\r\n").Disconnect();
        var busy = new FakeTransport { FailOpen = true };
        var second = new FakeTransport().Enqueue("ST,GS,+002000.0kg\r\n");
        using var svc = new IndicatorService(Profile(), new FakeFactory(first, busy, second));
        var events = new List<IndicatorConnectionEvent>();
        svc.ConnectionChanged += (_, e) => { lock (events) events.Add(e); };
        svc.Start();
        Wait.Until(() => svc.Current.Latest?.Value == 2000m, 8000, "reading after reconnect");
        Assert.True(svc.Current.ReconnectCount >= 2);
        lock (events)
        {
            Assert.Contains(events, e => e.State == ConnectionState.Error && e.Message.Contains("cable"));
            Assert.Contains(events, e => e.State == ConnectionState.Error && e.Message.Contains("busy"));
        }
    }

    [Fact]
    public void Poll_mode_sends_request_command()
    {
        var t = new FakeTransport();
        var profile = Profile() with { Poll = new PollSettings { Enabled = true, Command = "R\\r\\n", IntervalMs = 50 } };
        using var svc = new IndicatorService(profile, new FakeFactory(t));
        svc.Start();
        Wait.Until(() => { lock (t.Written) return t.Written.Count >= 3; });
        lock (t.Written) Assert.Equal("R\r\n"u8.ToArray(), t.Written[0]);
    }

    [Fact]
    public void Invalid_profile_is_refused()
    {
        var bad = Profile() with { Parser = new ParserOptions { Mode = ParserMode.Regex, Pattern = "no-weight-group" } };
        Assert.Throws<ArgumentException>(() => new IndicatorService(bad, new FakeFactory(new FakeTransport())));
    }

    [Fact]
    public async Task Tcp_serial_server_transport()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            for (var i = 0; i < 20; i++)
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"ST,GS,+{30000 + i * 10:000000.0}kg\r\n"));
                await Task.Delay(20);
            }
            await Task.Delay(500);
        });

        var profile = Profile(TransportKind.TcpClient) with { Connection = Profile().Connection with { Transport = TransportKind.TcpClient, Tcp = new TcpSettings { Host = "127.0.0.1", Port = port } } };
        using var svc = new IndicatorService(profile, new TransportFactory());
        svc.Start();
        Wait.Until(() => svc.Current.FramesParsed >= 10, because: "TCP frames");
        Assert.Equal(ConnectionState.Connected, svc.Current.Connection);
        Assert.StartsWith("tcp://127.0.0.1", svc.Current.PortDescription);
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// End-to-end through the real System.IO.Ports stack: socat creates a linked pair of pseudo-terminals (a virtual
    /// null-modem cable). The test writes indicator frames into one end; the service reads the other end as a COM port.
    /// Skipped where socat is unavailable (e.g. Windows CI, where com0com would be the equivalent).
    /// </summary>
    [SkippableSerialFact]
    public void Real_serial_port_through_virtual_null_modem()
    {
        var dir = Directory.CreateTempSubdirectory("awms-pty");
        var a = Path.Combine(dir.FullName, "indicator");
        var b = Path.Combine(dir.FullName, "pc");
        using var socat = Process.Start(new ProcessStartInfo("socat", $"-d -d pty,raw,echo=0,link={a} pty,raw,echo=0,link={b}") { RedirectStandardError = true })!;
        try
        {
            Wait.Until(() => File.Exists(a) && File.Exists(b), 5000, "pty links");
            var profile = Profile() with { Connection = Profile().Connection with { Serial = new SerialSettings { PortName = b, BaudRate = 9600, DtrEnable = false, RtsEnable = false } } };
            using var svc = new IndicatorService(profile, new TransportFactory());
            svc.Start();
            Wait.Until(() => svc.Current.Connection is ConnectionState.Connecting or ConnectionState.Silent or ConnectionState.Connected, 5000, "port opened");

            using var indicator = new FileStream(a, FileMode.Open, FileAccess.Write);
            for (var i = 0; i < 30 && svc.Current.FramesParsed < 5; i++)
            {
                indicator.Write(Encoding.ASCII.GetBytes($"ST,GS,+{12340 + i:000000.0}kg\r\n"));
                indicator.Flush();
                Thread.Sleep(50);
            }
            Wait.Until(() => svc.Current.FramesParsed >= 5, 5000, $"serial frames (state {svc.Current.Connection}, error {svc.Current.LastError})");
            Assert.Equal(ConnectionState.Connected, svc.Current.Connection);
            Assert.InRange(svc.Current.Latest!.Value!.Value, 12340m, 12370m);
        }
        finally
        {
            if (!socat.HasExited) socat.Kill();
            dir.Delete(true);
        }
    }
}

public sealed class SkippableSerialFactAttribute : FactAttribute
{
    public SkippableSerialFactAttribute()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/socat")) Skip = "socat (virtual serial ports) is not available on this machine.";
    }
}
