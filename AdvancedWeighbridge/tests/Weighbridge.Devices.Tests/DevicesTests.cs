using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Weighbridge.Core.Automation;
using Weighbridge.Core.Indicators;
using Weighbridge.Core.Indicators.Learning;
using Weighbridge.Core.Indicators.Parsing;
using Weighbridge.Core.Indicators.Protocols;
using Weighbridge.Devices.Cameras;
using Weighbridge.Devices.Discovery;
using Weighbridge.Devices.Identification;
using Weighbridge.Devices.IO;
using Weighbridge.Devices.Plugins;
using Weighbridge.Devices.Simulation;
using Weighbridge.Devices.Transports;

namespace Weighbridge.Devices.Tests;

public class SimulatorTests
{
    [Fact]
    public void Truck_cycle_produces_unstable_then_stable_weights_and_round_trips_through_parser()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero));
        var world = new SimulatorWorld(new SimulatorSettings(), time) { AutoCycle = true };
        var arrivals = new List<string>();
        world.VehicleArriving += (v, loaded) => arrivals.Add($"{v.Registration}:{loaded}");
        var parser = new WeightParser(new ParserOptions());
        var phases = new HashSet<SimPhase>();
        decimal maxStable = 0;

        for (var i = 0; i < 400; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(100));
            var r = world.Read();
            phases.Add(r.Phase);
            var frame = SimulatorWorld.Format("{status},{mode},{weight:+000000.0}kg\\r\\n", r);
            var text = Encoding.ASCII.GetString(frame);
            Assert.EndsWith("\r\n", text);
            var parsed = parser.Parse(Core.Indicators.Framing.RawFrame.FromText(text.TrimEnd(), time.GetUtcNow())).Reading!;
            Assert.Equal(r.Kg, parsed.Value);
            Assert.Equal(!r.Moving, parsed.IndicatorStable);
            if (r.Phase == SimPhase.OnDeck) maxStable = Math.Max(maxStable, r.Kg);
            Assert.Equal(0, r.Kg % 10); // rounded to the 10 kg division
        }
        Assert.Contains(SimPhase.Settling, phases);
        Assert.Contains(SimPhase.OnDeck, phases);
        Assert.Contains(SimPhase.Leaving, phases);
        Assert.Equal("WP CAB-1234:True", arrivals[0]);
        Assert.InRange(maxStable, 32480m, 32520m);
    }

    [Fact]
    public void Waits_for_exit_barrier_when_requested()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var world = new SimulatorWorld(new SimulatorSettings(), time) { WaitForExitBarrier = true };
        world.DriveOn(world.Fleet[1], loaded: false);
        for (var i = 0; i < 300; i++) { time.Advance(TimeSpan.FromMilliseconds(100)); world.Read(); }
        world.AutoCycle = true;
        for (var i = 0; i < 100; i++) { time.Advance(TimeSpan.FromMilliseconds(100)); world.Read(); }
        Assert.Equal(SimPhase.OnDeck, world.Phase);
        world.ExitBarrierOpen = true;
        for (var i = 0; i < 30; i++) { time.Advance(TimeSpan.FromMilliseconds(100)); world.Read(); }
        Assert.Equal(SimPhase.Leaving, world.Phase);
    }

    [Fact]
    public void Toledo_template_and_overload()
    {
        var bytes = SimulatorWorld.Format("@toledo+cs", new SimulatorReading(12500, false, false, SimPhase.OnDeck));
        Assert.Equal(0x02, bytes[0]);
        Assert.Equal(18, bytes.Length);
        var ol = SimulatorWorld.Format("{status} {weight}", new SimulatorReading(99999, false, true, SimPhase.OnDeck));
        Assert.Equal("OL OL", Encoding.ASCII.GetString(ol));
    }

    [Fact]
    public void Simulator_transport_through_indicator_service()
    {
        var profile = new IndicatorProfile
        {
            Name = "Sim",
            Connection = new ConnectionSettings { Transport = TransportKind.Simulator, Simulator = new SimulatorSettings { IntervalMs = 20 } },
        };
        using var svc = new Indicator.IndicatorService(profile, new TransportFactory());
        svc.Start();
        Wait.Until(() => svc.Current.FramesParsed > 10, because: "simulated frames");
        Assert.Equal(ConnectionState.Connected, svc.Current.Connection);
        Assert.Equal(0, svc.Current.FramesRejected);
    }
}

public class ReplayTests
{
    [Fact]
    public void Record_and_replay_capture()
    {
        var path = Path.Combine(Path.GetTempPath(), $"awms-{Guid.NewGuid():N}.wbcap");
        try
        {
            using (var rec = new RawCaptureRecorder(path, "COM3 9600 8N1", "unit test"))
            {
                rec.Record("ST,GS,+0012"u8);
                rec.Record("50.0kg\r\nST,GS,+001260.0kg\r\n"u8);
                Assert.Equal(38, rec.BytesRecorded);
            }
            var (header, records) = RawCaptureFormat.Load(path);
            Assert.Equal("COM3 9600 8N1", header.Source);
            Assert.Equal(2, records.Count);

            var profile = new IndicatorProfile { Connection = new ConnectionSettings { Transport = TransportKind.Replay, ReplayFile = path } };
            using var svc = new Indicator.IndicatorService(profile, new TransportFactory());
            svc.Start();
            Wait.Until(() => svc.Current.FramesParsed >= 2, because: "replayed frames");
            Assert.Contains(svc.RecentFrames(), f => f.Result.Reading?.Value == 1250m);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class BaudScannerTests
{
    [Fact]
    public async Task Finds_the_baud_rate_with_clean_data()
    {
        var scanner = new BaudScanner(s => s.BaudRate == 4800 && s.DataBits == 7 && s.Parity == ParityKind.Even
            ? new FakeTransport().Enqueue("ST,GS,+012500.0kg\r\nST,GS,+012500.0kg\r\nST,GS,+012500.0kg\r\n")
            : new FakeTransport().Enqueue(new byte[] { 0xF0, 0x81, 0x00, 0x9E, 0xFF, 0x13, 0xC7, 0xE2, 0x00, 0x88 }));
        var results = await scanner.ScanAsync("COM1", listenMs: 60);
        Assert.Equal(4800, results[0].Settings.BaudRate);
        Assert.Equal(ParityKind.Even, results[0].Settings.Parity);
        Assert.True(results[0].LooksValid);
        Assert.False(results[1].LooksValid);
    }

    [Fact]
    public void Port_listing_does_not_throw()
    {
        var ports = PortDiscovery.List();
        Assert.NotNull(ports);
        Assert.True(PortDiscovery.KnownUsbSerialVendors.ContainsKey("1A86"));
    }
}

/// <summary>Minimal Modbus TCP slave (functions 01, 02, 05) for tests.</summary>
internal sealed class FakeModbusServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    public bool[] Coils { get; } = new bool[32];
    public bool[] Inputs { get; } = new bool[32];
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public FakeModbusServer()
    {
        _listener.Start();
        _ = Task.Run(Loop);
    }

    private async Task Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch (OperationCanceledException) { return; }
            _ = Task.Run(async () =>
            {
                using var c = client;
                var s = c.GetStream();
                var header = new byte[7];
                try
                {
                    while (true)
                    {
                        await s.ReadExactlyAsync(header);
                        var len = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
                        var pdu = new byte[len - 1];
                        await s.ReadExactlyAsync(pdu);
                        byte[] reply;
                        var start = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1));
                        switch (pdu[0])
                        {
                            case 0x05:
                                if (start >= Coils.Length) { reply = [0x85, 0x02]; break; }
                                Coils[start] = pdu[3] == 0xFF;
                                reply = pdu;
                                break;
                            case 0x01 or 0x02:
                                var count = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3));
                                var src = pdu[0] == 0x01 ? Coils : Inputs;
                                var bytes = new byte[(count + 7) / 8];
                                for (var i = 0; i < count; i++) if (src[start + i]) bytes[i / 8] |= (byte)(1 << (i % 8));
                                reply = [pdu[0], (byte)bytes.Length, .. bytes];
                                break;
                            default:
                                reply = [(byte)(pdu[0] | 0x80), 0x01];
                                break;
                        }
                        var adu = new byte[7 + reply.Length];
                        header.AsSpan(0, 4).CopyTo(adu);
                        BinaryPrimitives.WriteUInt16BigEndian(adu.AsSpan(4), (ushort)(reply.Length + 1));
                        adu[6] = header[6];
                        reply.CopyTo(adu, 7);
                        await s.WriteAsync(adu);
                    }
                }
                catch (Exception e) when (e is IOException or EndOfStreamException or ObjectDisposedException) { }
            });
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}

public class IoTests
{
    [Fact]
    public async Task Modbus_tcp_lights_barrier_and_sensors()
    {
        using var server = new FakeModbusServer();
        var config = new HardwareConfig
        {
            Controllers = [new IoControllerConfig { Id = "m1", Kind = IoControllerKind.ModbusTcp, Host = "127.0.0.1", Port = server.Port }],
            Outputs = new()
            {
                [OutputSignal.EntryRed] = new("m1", 0), [OutputSignal.EntryAmber] = new("m1", 1), [OutputSignal.EntryGreen] = new("m1", 2),
                [OutputSignal.ExitRed] = new("m1", 3), [OutputSignal.ExitAmber] = new("m1", 4), [OutputSignal.ExitGreen] = new("m1", 5),
                [OutputSignal.ExitBarrierOpen] = new("m1", 6), [OutputSignal.Alarm] = new("m1", 7, Invert: true),
            },
            Inputs = new()
            {
                [InputSignal.Presence] = new("m1", 0),
                [InputSignal.EntryBeamBlocked] = new("m1", 1),
                [InputSignal.ExitBeamBlocked] = new("m1", 2),
                [InputSignal.SafetyOk] = new("m1", 3),
            },
        };
        using var hw = new HardwareController(config, HardwareController.DefaultFactory(null));

        await hw.ExecuteAsync(new SetLightsCommand(LightColor.Green, LightColor.Red));
        Assert.Equal([false, false, true, true, false, false], server.Coils[..6]);
        await hw.ExecuteAsync(new BarrierCommand(BarrierId.Exit, true));
        Assert.True(server.Coils[6]);
        await hw.ExecuteAsync(new AlarmCommand(false));
        Assert.True(server.Coils[7]); // inverted output

        server.Inputs[0] = true;
        server.Inputs[1] = true;
        server.Inputs[3] = true;
        var sensors = await hw.ReadSensorsAsync();
        Assert.True(sensors.Presence);
        Assert.False(sensors.PositionOk);
        Assert.True(sensors.SafetyOk);
        server.Inputs[1] = false;
        Assert.True((await hw.ReadSensorsAsync()).PositionOk);
        Assert.Equal(LightColor.Green, hw.Status.EntryLight);
        Assert.True(hw.Status.ExitBarrierOpen);
    }

    [Fact]
    public async Task Modbus_exception_and_unreachable_module_fail_safe()
    {
        using var server = new FakeModbusServer();
        using var client = new ModbusTcpClient("127.0.0.1", server.Port, 1);
        await Assert.ThrowsAsync<ModbusException>(() => client.WriteCoilAsync(500, true, default));

        var config = new HardwareConfig
        {
            Controllers = [new IoControllerConfig { Id = "dead", Kind = IoControllerKind.ModbusTcp, Host = "127.0.0.1", Port = 1, TimeoutMs = 300 }],
            Inputs = new() { [InputSignal.SafetyOk] = new("dead", 0) },
        };
        using var hw = new HardwareController(config, HardwareController.DefaultFactory(null));
        var sensors = await hw.ReadSensorsAsync();
        Assert.False(sensors.SafetyOk); // unreadable safety input = not safe
        Assert.NotNull(sensors.Error);
    }

    [Fact]
    public void Modbus_pdu_encoding()
    {
        Assert.Equal(new byte[] { 0x05, 0x00, 0x07, 0xFF, 0x00 }, ModbusPdu.WriteCoil(7, true));
        Assert.Equal(new byte[] { 0x02, 0x00, 0x00, 0x00, 0x0A }, ModbusPdu.ReadBits(ModbusPdu.ReadDiscreteInputs, 0, 10));
        var bits = ModbusPdu.ParseBits(0x02, new byte[] { 0x02, 0x02, 0b0000_0101, 0b0000_0010 }, 10);
        Assert.Equal([true, false, true, false, false, false, false, false, false, true], bits);
        Assert.Throws<ModbusException>(() => ModbusPdu.ParseBits(0x02, new byte[] { 0x82, 0x02 }, 1));
        Assert.Equal(7, ModbusPdu.RtuResponseLength(0x02, 16));
    }

    [Fact]
    public async Task Simulated_io_drives_the_simulator_barrier()
    {
        var world = new SimulatorWorld(new SimulatorSettings());
        using var hw = new HardwareController(HardwareConfig.CreateSimulated(), HardwareController.DefaultFactory(world));
        await hw.SetBarrierAsync(BarrierId.Exit, true);
        Assert.True(world.ExitBarrierOpen);
        var sensors = await hw.ReadSensorsAsync();
        Assert.False(sensors.Presence);
        Assert.True(sensors.SafetyOk);
    }
}

public class CameraAndIdentificationTests
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46];

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            return Task.FromResult(respond(request));
        }
    }

    [Fact]
    public async Task Snapshot_camera_accepts_only_images()
    {
        var ok = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Jpeg) });
        using var cam = new HttpSnapshotCamera(new CameraConfig { Name = "Front", Auth = CameraAuth.None }, ok);
        var snap = await cam.CaptureAsync(default);
        Assert.Equal("image/jpeg", snap.ContentType);
        Assert.Equal(Jpeg, snap.Data);

        var html = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>login</html>") });
        using var bad = new HttpSnapshotCamera(new CameraConfig { Auth = CameraAuth.None }, html);
        await Assert.ThrowsAsync<IOException>(() => bad.CaptureAsync(default));

        var denied = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var auth = new HttpSnapshotCamera(new CameraConfig { Auth = CameraAuth.None }, denied);
        var e = await Assert.ThrowsAsync<IOException>(() => auth.CaptureAsync(default));
        Assert.Contains("authentication", e.Message);
    }

    [Fact]
    public async Task Anpr_receiver_accepts_json_and_xml_pushes()
    {
        using var rx = new AnprHttpReceiver(new IdentificationSourceConfig { Kind = IdentificationKind.AnprHttp, ListenAddress = "127.0.0.1", ListenPort = 0, SharedToken = "s3cret", DebounceMs = 0 });
        var plates = new List<VehicleIdentification>();
        rx.Identified += (_, id) => { lock (plates) plates.Add(id); };
        rx.Start();
        using var http = new HttpClient();
        var url = $"http://127.0.0.1:{rx.BoundPort}/anpr?token=s3cret";

        var r1 = await http.PostAsync(url, new StringContent("""{"event":"anpr","plate":"wp cab-1234","confidence":92}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        await http.PostAsync(url, new StringContent("<EventNotificationAlert><ANPR><licensePlate>NC LK-5521</licensePlate><confidenceLevel>88</confidenceLevel></ANPR></EventNotificationAlert>", Encoding.UTF8, "application/xml"));
        await http.PostAsync(url, new StringContent("""{"plate":"ABC-1","confidence":0.3}""", Encoding.UTF8, "application/json"));
        var forbidden = await http.PostAsync($"http://127.0.0.1:{rx.BoundPort}/anpr", new StringContent("""{"plate":"EVIL-1"}"""));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        await Wait.UntilAsync(() => { lock (plates) return plates.Count >= 2; });
        await Task.Delay(200);
        lock (plates)
        {
            Assert.Equal(["WP CAB-1234", "NC LK-5521"], plates.Select(p => p.Registration));
            Assert.Equal(0.92, plates[0].Confidence!.Value, 3);
            Assert.All(plates, p => Assert.Equal("ANPR", p.Source));
        }
    }

    [Fact]
    public void Rfid_reader_maps_tags_and_debounces()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var tags = new Dictionary<string, string> { ["E2000017221101441890ABCD"] = "wp cab-1234" };
        using var src = new SerialIdentificationSource(new IdentificationSourceConfig { Kind = IdentificationKind.Rfid, DebounceMs = 3000 },
            new TransportFactory(), tag => tags.GetValueOrDefault(tag), NullLogger.Instance, time);
        var ids = new List<VehicleIdentification>();
        var unknown = new List<string>();
        src.Identified += (_, id) => ids.Add(id);
        src.UnknownCode += (_, c) => unknown.Add(c);

        src.Process("E2000017221101441890ABCD\r");
        src.Process("E2000017221101441890ABCD");
        time.Advance(TimeSpan.FromSeconds(4));
        src.Process("E2000017221101441890ABCD");
        src.Process("FFFF0000");
        Assert.Equal(2, ids.Count);
        Assert.Equal("WP CAB-1234", ids[0].Registration);
        Assert.Equal("RFID", ids[0].Source);
        Assert.Equal(["FFFF0000"], unknown);
    }

    [Fact]
    public void Simulated_anpr_reports_arrivals()
    {
        var world = new SimulatorWorld(new SimulatorSettings(), new FakeTimeProvider(DateTimeOffset.UnixEpoch)) { AutoCycle = false };
        using var src = new SimulatedIdentificationSource(world);
        VehicleIdentification? got = null;
        src.Identified += (_, id) => got = id;
        src.Start();
        world.AutoCycle = true;
        for (var i = 0; i < 200 && got is null; i++) world.Read(DateTimeOffset.UnixEpoch.AddSeconds(i));
        Assert.Equal("WP CAB-1234", got?.Registration);
    }

    [Fact]
    public void Plugin_loader_handles_missing_and_bad_files()
    {
        var registry = ProtocolRegistry.CreateDefault();
        Assert.Empty(PluginLoader.LoadProtocols("/nonexistent/plugins", registry, NullLogger.Instance));
        var dir = Directory.CreateTempSubdirectory();
        File.WriteAllText(Path.Combine(dir.FullName, "broken.dll"), "not an assembly");
        Assert.Empty(PluginLoader.LoadProtocols(dir.FullName, registry, NullLogger.Instance));
        // The test assembly's referenced Core contains the built-in protocol; loading it registers nothing new but must not fail.
        Assert.True(registry.TryGet("toledo-continuous", out _));
        dir.Delete(true);
    }

    [Fact]
    public void Line_scores_rank_fake_scan()
    {
        Assert.True(LineQualityScorer.Score(new LineSettingsCandidate(9600, 8, ParityKind.None, StopBitsKind.One), "ST,GS,+012500.0kg\r\n"u8).Score > 0.6);
    }
}
