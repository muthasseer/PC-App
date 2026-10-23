using Microsoft.Extensions.Time.Testing;
using Weighbridge.Application.Security;
using Weighbridge.Application.Settings;
using Weighbridge.Application.Weighing;
using Weighbridge.Core.Indicators;
using Weighbridge.Core.Indicators.Framing;
using Weighbridge.Core.Indicators.Stability;
using Weighbridge.Core.Security;
using Weighbridge.Data;
using Weighbridge.Data.Stores;
using Weighbridge.Reporting.Tickets;

namespace Weighbridge.Application.Tests;

/// <summary>A fully initialised application on a temporary folder, with users for every role.</summary>
public sealed class TestHost : IAsyncDisposable
{
    public const string AdminPassword = "Weigh#2026x";
    public const string Password = "Pass#2026x";

    private TestHost(AppHost host, DirectoryInfo dir, FakeTimeProvider? time)
    {
        Host = host;
        Dir = dir;
        FakeTime = time;
    }

    public AppHost Host { get; }
    public DirectoryInfo Dir { get; }
    public FakeTimeProvider? FakeTime { get; }
    public UserContext Admin { get; private set; } = null!;
    public UserContext Operator { get; private set; } = null!;
    public UserContext Manager { get; private set; } = null!;
    public long MaterialId { get; private set; }
    public long CustomerId { get; private set; }

    public DateTimeOffset Now => Host.Time.GetUtcNow();

    public static async Task<TestHost> CreateAsync(bool fakeTime = true, IndicatorProfile? profile = null, HttpMessageHandler? cloud = null)
    {
        var dir = Directory.CreateTempSubdirectory("awms-app-");
        var time = fakeTime ? new FakeTimeProvider(new DateTimeOffset(2026, 3, 2, 2, 30, 0, TimeSpan.Zero)) : null;
        var host = await AppHost.CreateAsync(new AppHostOptions { Paths = new AppPaths(dir.FullName), Time = time, CloudHandler = cloud });
        var t = new TestHost(host, dir, time);
        await host.Setup.InitializeAsync(new SetupRequest
        {
            Company = new CompanySettings { Name = "Test Quarry", Address = "Main road" },
            AdminUsername = "admin",
            AdminDisplayName = "Admin",
            AdminPassword = AdminPassword,
            SiteCode = "QRY",
            WeighbridgeCode = "WB1",
            CapacityKg = 80000,
            MinimumCaptureKg = 200,
            Profile = profile,
        });
        t.Admin = (await host.Auth.LoginAsync("admin", AdminPassword)).User!;
        await host.Auth.CreateUserAsync(t.Admin, "op", "Operator One", Password, Roles.Operator, mustChangePassword: false);
        await host.Auth.CreateUserAsync(t.Admin, "mgr", "Manager One", Password, Roles.Manager, mustChangePassword: false);
        t.Operator = (await host.Auth.LoginAsync("op", Password)).User!;
        t.Manager = (await host.Auth.LoginAsync("mgr", Password)).User!;
        t.MaterialId = await host.MasterData.SaveMaterialAsync(t.Admin, new MaterialEntity { Code = "SAND", Name = "River sand" });
        t.CustomerId = await host.MasterData.SavePartyAsync(t.Admin, PartyKind.Customer, new PartyEntity { Code = "C1", Name = "Builder Ltd" });
        return t;
    }

    public void Advance(TimeSpan by) => FakeTime?.Advance(by);

    /// <summary>A stable, fresh reading as the indicator service would publish it.</summary>
    public IndicatorSnapshot Snapshot(decimal kg, bool stable = true, int ageMs = 50, long sequence = 100) => new()
    {
        Connection = ConnectionState.Connected,
        Latest = new WeightReading
        {
            Value = kg, Flags = stable ? ReadingFlags.Stable | ReadingFlags.Gross : ReadingFlags.Motion, Text = $"ST,GS,+{kg:000000.0}kg", ReceivedAt = Now.AddMilliseconds(-ageMs),
            Sequence = sequence, Frame = RawFrame.FromText($"ST,GS,+{kg:000000.0}kg", Now.AddMilliseconds(-ageMs), sequence),
        },
        Stability = stable ? new StabilityState(true, StabilityReason.Stable, 0, 2500, 25, kg) : new StabilityState(false, StabilityReason.OutsideTolerance, 120, 0, 4, kg),
        ProfileName = "Test profile",
        ProfileVersion = 3,
        ProfileId = Guid.NewGuid(),
        PortDescription = "COM3 9600 8N1",
        Timestamp = Now,
    };

    public TransactionDetails Details(string vehicle) => new() { VehicleReg = vehicle, MaterialId = MaterialId, CustomerId = CustomerId, Direction = Core.Weighing.TransactionDirection.Outbound };

    public Task<CaptureResult> CaptureAsync(UserContext user, string vehicle, decimal kg, bool stable = true, bool confirmDuplicate = false, bool storedTare = false) =>
        Host.Weighing.CaptureAsync(user, new CaptureRequest
        {
            Details = Details(vehicle), Mode = Core.Weighing.OperatingMode.SemiAutomatic, Snapshot = Snapshot(kg, stable),
            EvidenceFrames = [RawFrame.FromText($"ST,GS,+{kg:000000.0}kg", Now, 99), RawFrame.FromText($"ST,GS,+{kg:000000.0}kg", Now, 100)],
            ConfirmDuplicate = confirmDuplicate, UseStoredTare = storedTare,
        });

    public async Task<SupervisorApproval> ApprovalAsync(string permission) =>
        (await Host.Auth.VerifySupervisorAsync("mgr", Password, permission, "test"))!;

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Dir.Delete(true); }
        catch (IOException) { }
    }
}

public sealed class RecordingPrinter : IPrintService
{
    public List<(TicketData Data, int Copies)> Printed { get; } = [];

    public Task PrintAsync(TicketData data, TicketTemplate template, int copies, string? printerName, bool escPosRaw, CancellationToken ct)
    {
        Printed.Add((data, copies));
        return Task.CompletedTask;
    }
}
