using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Weighbridge.Application.Automation;
using Weighbridge.Application.Security;
using Weighbridge.Application.Settings;
using Weighbridge.Application.Weighing;
using Weighbridge.Core.Automation;
using Weighbridge.Core.Indicators;
using Weighbridge.Core.Security;
using Weighbridge.Core.Units;
using Weighbridge.Core.Weighing;
using Weighbridge.Data;
using Weighbridge.Desktop.Infrastructure;
using CaptureRequest = Weighbridge.Application.Weighing.CaptureRequest;

namespace Weighbridge.Desktop.ViewModels;

public sealed record Choice(long? Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// The weighing dashboard. The large display is refreshed ten times per second from the indicator snapshot; GET WEIGHT
/// sends the latest snapshot to the weighing service, which validates it (fresh, stable, within limits) and records it.
/// The display never shows an out-of-date or invalid value as the current weight.
/// </summary>
public sealed partial class WeighingViewModel : ObservableObject, IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly WpfPrintService _printer = new();
    private CaptureLimits _limits = new();
    private WeightDisplayOptions _display = WeightDisplayOptions.DefaultKg;
    private DateTimeOffset _capturedAt = DateTimeOffset.MinValue;
    private bool _busy;
    private CancellationTokenSource? _contextLookup;
    private CancellationTokenSource? _stableWait;

    public WeighingViewModel()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Refresh();
        if (App.Host.Automation is { } a)
        {
            a.AutoCaptured += OnAutoCaptured;
            a.PrintRequested += OnPrintRequested;
            a.StatusChanged += OnAutomationStatus;
        }
    }

    // ------------------------------------------------------------------ live display

    [ObservableProperty] public partial string WeightText { get; set; } = "------";
    [ObservableProperty] public partial string UnitText { get; set; } = "kg";
    [ObservableProperty] public partial string StateText { get; set; } = string.Empty;
    [ObservableProperty] public partial string StateSymbol { get; set; } = "⏻";
    [ObservableProperty] public partial Brush StateBrush { get; set; } = Brushes.DimGray;
    [ObservableProperty] public partial string DetailText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsSimulator { get; set; }
    [ObservableProperty] public partial bool CanCapture { get; set; }
    [ObservableProperty] public partial double WeightFontSize { get; set; } = 120;

    // ------------------------------------------------------------------ mode

    public ObservableCollection<OperatingMode> Modes { get; } = [];
    [ObservableProperty] public partial OperatingMode Mode { get; set; } = OperatingMode.SemiAutomatic;
    [ObservableProperty] public partial bool IsManualMode { get; set; }
    [ObservableProperty] public partial bool IsAutomaticMode { get; set; }

    // ------------------------------------------------------------------ transaction details

    [ObservableProperty] public partial string Vehicle { get; set; } = string.Empty;
    [ObservableProperty] public partial TransactionDirection Direction { get; set; } = TransactionDirection.Outbound;
    public ObservableCollection<Choice> Customers { get; } = [];
    public ObservableCollection<Choice> Suppliers { get; } = [];
    public ObservableCollection<Choice> Materials { get; } = [];
    public ObservableCollection<string> VehicleSuggestions { get; } = [];
    [ObservableProperty] public partial Choice? Customer { get; set; }
    [ObservableProperty] public partial Choice? Supplier { get; set; }
    [ObservableProperty] public partial Choice? Material { get; set; }
    [ObservableProperty] public partial string Driver { get; set; } = string.Empty;
    [ObservableProperty] public partial string DeliveryNote { get; set; } = string.Empty;
    [ObservableProperty] public partial string Reference { get; set; } = string.Empty;
    [ObservableProperty] public partial string Remarks { get; set; } = string.Empty;
    [ObservableProperty] public partial bool StoredTareAvailable { get; set; }
    [ObservableProperty] public partial bool UseStoredTare { get; set; }
    [ObservableProperty] public partial string StageText { get; set; } = string.Empty;
    [ObservableProperty] public partial string ContextText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool ContextWarning { get; set; }

    // ------------------------------------------------------------------ result and lists

    [ObservableProperty] public partial string ResultTitle { get; set; } = string.Empty;
    [ObservableProperty] public partial string ResultText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool ResultIsError { get; set; }
    [ObservableProperty] public partial long? LastTransactionId { get; set; }
    public ObservableCollection<TransactionEntity> Pending { get; } = [];
    public ObservableCollection<TransactionEntity> Recent { get; } = [];

    // ------------------------------------------------------------------ automation

    [ObservableProperty] public partial bool HasAutomation { get; set; }
    [ObservableProperty] public partial string AutomationText { get; set; } = string.Empty;
    [ObservableProperty] public partial string EntryLightText { get; set; } = string.Empty;
    [ObservableProperty] public partial string ExitLightText { get; set; } = string.Empty;
    [ObservableProperty] public partial Brush EntryLightBrush { get; set; } = Brushes.Gray;
    [ObservableProperty] public partial Brush ExitLightBrush { get; set; } = Brushes.Gray;
    [ObservableProperty] public partial string BarrierText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool HasFault { get; set; }

    private static UserContext User => App.Host.Session.Require();

    public async Task LoadAsync()
    {
        var host = App.Host;
        _limits = await host.Weighing.LimitsAsync();
        var loc = await host.Settings.GetAsync<LocalizationSettings>(SettingKeys.Localization);
        _display = loc.Display;
        WeightFontSize = loc.WeightFontSize;
        UnitText = WeightUnits.Symbol(_display.Unit);

        Modes.Clear();
        var user = User;
        if (user.Has(Permissions.WeighingAutomatic)) Modes.Add(OperatingMode.Automatic);
        if (user.Has(Permissions.WeighingCapture)) Modes.Add(OperatingMode.SemiAutomatic);
        if (user.Has(Permissions.WeighingManualEntry)) Modes.Add(OperatingMode.Manual);
        var current = host.Automation?.Mode ?? OperatingMode.SemiAutomatic;
        Mode = Modes.Contains(current) ? current : Modes.FirstOrDefault();

        await LoadMastersAsync();
        await RefreshListsAsync();
        _timer.Start();
    }

    public void Pause() => _timer.Stop();

    private async Task LoadMastersAsync()
    {
        var md = App.Host.MasterData;
        var user = User;
        Fill(Customers, (await md.SearchPartiesAsync(user, Data.PartyKind.Customer, null, 1, 1000)).Items.Select(p => new Choice(p.Id, p.Name)));
        Fill(Suppliers, (await md.SearchPartiesAsync(user, Data.PartyKind.Supplier, null, 1, 1000)).Items.Select(p => new Choice(p.Id, p.Name)));
        Fill(Materials, (await md.SearchMaterialsAsync(user, null, 1, 1000)).Items.Select(m => new Choice(m.Id, m.Name)));
        var vehicles = await md.SearchVehiclesAsync(user, null, 1, 2000);
        VehicleSuggestions.Clear();
        foreach (var v in vehicles.Items) VehicleSuggestions.Add(v.Registration);
    }

    private static void Fill(ObservableCollection<Choice> target, IEnumerable<Choice> items)
    {
        target.Clear();
        target.Add(new Choice(null, "—"));
        foreach (var i in items) target.Add(i);
    }

    public async Task RefreshListsAsync()
    {
        var history = App.Host.History;
        var pending = await history.PendingAsync();
        var recent = await history.RecentAsync(25);
        Pending.Clear();
        foreach (var t in pending) Pending.Add(t);
        Recent.Clear();
        foreach (var t in recent) Recent.Add(t);
    }

    // ------------------------------------------------------------------ live refresh

    private void Refresh()
    {
        var host = App.Host;
        var snap = host.Indicator.Snapshot;
        var now = host.Time.GetUtcNow();
        IsSimulator = host.Indicator.Profile?.Connection.Transport == TransportKind.Simulator;
        var status = DisplayStatusEvaluator.Evaluate(snap, now, _limits, Mode, (now - _capturedAt).TotalSeconds < 3);
        StateBrush = StateBrushes.For(status.Color);
        StateSymbol = StateBrushes.Symbol(status.Color);
        StateText = Loc.T(status.TextKey, status.Detail.ToUpperInvariant());

        var reading = snap.Latest;
        var usable = reading is { IsUsable: true, Weight: { } } && status.Condition is not (DisplayCondition.Stale or DisplayCondition.Invalid or DisplayCondition.Disconnected or DisplayCondition.NoData);
        if (Mode == OperatingMode.Manual && !usable)
        {
            WeightText = "MANUAL";
        }
        else if (usable)
        {
            WeightText = _display.FormatNumber(reading!.Weight!.Value);
        }
        else
        {
            WeightText = status.Condition == DisplayCondition.Overload ? "OVERLOAD" : "------";
        }

        var age = snap.AgeMs(now);
        var parts = new List<string>();
        if (snap.PortDescription is { } port) parts.Add(port);
        if (snap.ProfileName is { } profile) parts.Add($"{profile} v{snap.ProfileVersion}");
        if (age is { } a) parts.Add(string.Create(CultureInfo.InvariantCulture, $"age {a:0} ms"));
        if (snap.Stability is { } st && reading is not null) parts.Add(st.IsStable ? $"stable for {st.StableForMs / 1000.0:0.0} s" : st.Reason.ToString());
        if (!usable && reading?.Weight is { } last && age is { } la) parts.Add($"last value {_display.Format(last)} {la / 1000:0.0} s ago");
        if (status.Color == DisplayColor.Red) parts.Add(status.Detail);
        DetailText = string.Join("  ·  ", parts);
        CanCapture = !_busy && Mode != OperatingMode.Manual && snap.Connection == ConnectionState.Connected;
    }

    // ------------------------------------------------------------------ mode

    partial void OnModeChanged(OperatingMode value)
    {
        IsManualMode = value == OperatingMode.Manual;
        IsAutomaticMode = value == OperatingMode.Automatic;
        if (App.Host.Automation is { } a && a.Mode != value)
        {
            _ = Dialogs.RunAsync(async () =>
            {
                await a.SetModeAsync(User, value);
                if (value == OperatingMode.Automatic) ShowResult("Automatic mode", "Vehicles are identified, weighed and released without operator action. Every step is recorded in the audit trail.", false);
            });
        }
    }

    // ------------------------------------------------------------------ vehicle context

    partial void OnVehicleChanged(string value)
    {
        _contextLookup?.Cancel();
        var cts = _contextLookup = new CancellationTokenSource();
        _ = LookupAsync(value, cts.Token);
    }

    private async Task LookupAsync(string registration, CancellationToken ct)
    {
        try
        {
            await Task.Delay(250, ct);
            var context = await App.Host.Weighing.VehicleContextAsync(registration, ct);
            if (ct.IsCancellationRequested) return;
            StoredTareAvailable = context.StoredTareUsable && context.Pending is null;
            if (!StoredTareAvailable) UseStoredTare = false;
            ContextWarning = context.Warning is not null;
            if (context.Pending is { } p)
            {
                StageText = Loc.T("w.second", "Second weighing");
                ContextText = $"Ticket {p.TicketNo}: first weight {_display.Format(p.First!.Value)} at {p.CreatedUtc.ToLocalTime():HH:mm} ({p.MaterialName ?? "no material"}, {p.CustomerName ?? p.SupplierName ?? "no party"}).";
                Customer = Customers.FirstOrDefault(c => c.Id == p.CustomerId) ?? Customer;
                Supplier = Suppliers.FirstOrDefault(c => c.Id == p.SupplierId) ?? Supplier;
                Material = Materials.FirstOrDefault(c => c.Id == p.MaterialId) ?? Material;
                Direction = p.Direction;
            }
            else
            {
                StageText = string.IsNullOrWhiteSpace(registration) ? string.Empty : Loc.T("w.first", "First weighing");
                ContextText = context.Vehicle?.StoredTare is { } tare && StoredTareAvailable ? $"Approved stored tare {_display.Format(tare)}." : string.Empty;
            }
            if (context.Warning is { } w) ContextText = (w + " " + ContextText).Trim();
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (UiErrors.IsUserFacing(e))
        {
            ContextText = e.Message;
            ContextWarning = true;
        }
    }

    private TransactionDetails Details() => new()
    {
        VehicleReg = Vehicle.Trim(),
        Direction = Direction,
        CustomerId = Customer?.Id,
        SupplierId = Supplier?.Id,
        MaterialId = Material?.Id,
        DriverName = Blank(Driver),
        DeliveryNoteNo = Blank(DeliveryNote),
        ReferenceNo = Blank(Reference),
        Remarks = Blank(Remarks),
    };

    // ------------------------------------------------------------------ GET WEIGHT

    [RelayCommand]
    private async Task GetWeightAsync()
    {
        if (_busy || Mode == OperatingMode.Manual) return;
        _busy = true;
        try
        {
            var host = App.Host;
            var snapshot = host.Indicator.Snapshot; // the latest validated snapshot at the moment of the click
            var weighing = await host.Settings.GetAsync<WeighingSettings>(SettingKeys.Weighing);
            var frames = snapshot.Latest is { } r ? host.Indicator.Current?.EvidenceFor(r.Sequence, 5) ?? [] : [];
            var (images, cameraErrors) = host.Cameras.Cameras.Count == 0
                ? ([], [])
                : await host.Cameras.CaptureAllAsync(TimeSpan.FromMilliseconds(weighing.CameraCaptureTimeoutMs));
            var request = new CaptureRequest
            {
                Details = Details(), Mode = Mode, Snapshot = snapshot, EvidenceFrames = frames, Images = images, UseStoredTare = UseStoredTare,
            };
            var result = await host.Weighing.CaptureAsync(User, request);
            if (result.Failure == CaptureFailure.WaitForStability)
            {
                // Policy "wait for stable": capture the first stable reading within the timeout (Esc cancels).
                var (stable, cancelled) = await WaitForStableAsync(weighing.Capture.WaitForStableTimeoutMs);
                if (stable is null)
                {
                    ShowResult("Not captured", cancelled ? "Cancelled." : "The weight did not become stable in time. Check that the vehicle is standing still and press GET WEIGHT again.", true);
                    return;
                }
                var stableFrames = stable.Latest is { } sr ? host.Indicator.Current?.EvidenceFor(sr.Sequence, 5) ?? [] : [];
                request = request with { Snapshot = stable, EvidenceFrames = stableFrames };
                result = await host.Weighing.CaptureAsync(User, request);
            }
            if (result.Failure == CaptureFailure.DuplicateSuspected && Dialogs.Confirm(result.Message + "\n\nRecord it anyway?"))
            {
                result = await host.Weighing.CaptureAsync(User, request with { ConfirmDuplicate = true });
            }
            if (result.Failure == CaptureFailure.OverrideRequired)
            {
                var approvals = await host.Settings.GetAsync<ApprovalSettings>(SettingKeys.Approvals);
                var sup = Dialogs.Supervisor($"Capture an UNSTABLE weight: {result.Message}", approvals.MinimumReasonLength);
                if (sup is null) { ShowResult("Not captured", result.Message, true); return; }
                var approval = await host.Auth.VerifySupervisorAsync(sup.Username, sup.Password, Permissions.WeighingOverrideUnstable, "Unstable weight override");
                if (approval is null) { ShowResult("Not captured", "The supervisor credentials were not accepted or the supervisor lacks the permission.", true); return; }
                // The override applies to the weight shown when the operator asked for it; a newer snapshot is re-validated.
                result = await host.Weighing.CaptureAsync(User, request with { OverrideApproval = approval, OverrideReason = sup.Reason });
            }
            await HandleResultAsync(result, cameraErrors);
        }
        catch (Exception e) when (UiErrors.IsUserFacing(e))
        {
            ShowResult("Not captured", e.Message, true);
        }
        finally
        {
            _busy = false;
        }
    }

    [RelayCommand]
    private async Task ManualEntryAsync()
    {
        var host = App.Host;
        var approvals = await host.Settings.GetAsync<ApprovalSettings>(SettingKeys.Approvals);
        var d = new FormDialog(Loc.T("w.manualEntry", "Manual weight entry"), Loc.T("w.manualWarning", "Manual weight – not a verified indicator measurement"), "Record manual weight", danger: true);
        d.AddNote("The weight is recorded as MANUAL on the ticket, in reports and in the audit trail. Use only when the indicator cannot be used.", warning: true);
        var weight = d.AddText("Weight read from the indicator display (kg)");
        var reason = d.AddText(Loc.T("common.reason", "Reason"), multiline: true);
        d.Validate = () =>
        {
            if (!decimal.TryParse(weight.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var kg) || kg <= 0) return "Enter the weight in kilograms.";
            return reason.Text.Trim().Length < approvals.MinimumReasonLength ? $"Enter a reason of at least {approvals.MinimumReasonLength} characters." : null;
        };
        if (!d.ShowModal()) return;
        var kgValue = decimal.Parse(weight.Text, NumberStyles.Number, CultureInfo.CurrentCulture);
        await Dialogs.RunAsync(async () =>
        {
            SupervisorApproval? approval = null;
            if (approvals.ManualEntryRequiresSupervisor)
            {
                var sup = Dialogs.Supervisor($"Manual weight {kgValue:#,0.##} kg for {Vehicle}", 0);
                if (sup is null) return;
                approval = await host.Auth.VerifySupervisorAsync(sup.Username, sup.Password, Permissions.WeighingManualEntry, "Manual weight entry");
                if (approval is null) { ShowResult("Not recorded", "The supervisor credentials were not accepted.", true); return; }
            }
            var request = new ManualEntryRequest { Details = Details(), WeightKg = kgValue, Reason = reason.Text.Trim(), Approval = approval };
            var result = await host.Weighing.ManualEntryAsync(User, request);
            if (result.Failure == CaptureFailure.DuplicateSuspected && Dialogs.Confirm(result.Message + "\n\nRecord it anyway?"))
            {
                result = await host.Weighing.ManualEntryAsync(User, request with { ConfirmDuplicate = true });
            }
            await HandleResultAsync(result, []);
        });
    }

    private async Task HandleResultAsync(CaptureResult result, IReadOnlyList<string> cameraErrors)
    {
        if (!result.Success)
        {
            ShowResult(result.Failure == CaptureFailure.WaitForStability ? Loc.T("status.waitStable", "Waiting for stable weight") : "Not captured", result.Message, true);
            return;
        }
        _capturedAt = App.Host.Time.GetUtcNow();
        var t = result.Transaction!;
        LastTransactionId = t.Id;
        var lines = new List<string>();
        if (result.IsFinal)
        {
            lines.Add($"{Loc.T("w.gross", "Gross")}: {_display.Format(t.Gross!.Value)}   {Loc.T("w.tare", "Tare")}: {_display.Format(t.Tare!.Value)}   {Loc.T("w.net", "Net")}: {_display.Format(t.Net!.Value)}");
        }
        else
        {
            lines.Add($"{Loc.T("w.first", "First weighing")}: {_display.Format(t.First!.Value)} – waiting for the second weighing.");
        }
        lines.AddRange(result.Warnings);
        lines.AddRange(cameraErrors.Select(e => "Camera: " + e));
        ShowResult($"{Loc.T("tx.ticket", "Ticket")} {t.TicketNo} – {(result.IsFinal ? Loc.T("tx.Completed", "Completed") : Loc.T("tx.Pending", "Pending"))}", string.Join("\n", lines), false);
        if (App.Host.Automation is { } a) await a.NotifyOperatorCaptureAsync(result);
        await RefreshListsAsync();
        await MaybePrintAsync(t.Id, result.IsFinal, automatic: false);
        if (result.IsFinal || Mode != OperatingMode.Automatic) ClearDetails(keepResult: true);
    }

    private async Task MaybePrintAsync(long transactionId, bool final, bool automatic)
    {
        var printing = await App.Host.Settings.GetAsync<PrintingSettings>(SettingKeys.Printing);
        if (!(final ? printing.PrintOnCompletion : printing.PrintOnFirstWeighing)) return;
        if (!automatic && printing.ConfirmBeforePrint && !Dialogs.Confirm(Loc.T("w.print", "Print ticket") + "?")) return;
        await PrintAsync(transactionId);
    }

    private async Task PrintAsync(long transactionId)
    {
        try
        {
            var outcome = await App.Host.Tickets.PrintAsync(User, transactionId, _printer);
            if (outcome.PdfPath is not null) App.Log.LogInformationSafe($"Ticket printed; PDF copy {outcome.PdfPath}");
        }
        catch (Exception e) when (UiErrors.IsUserFacing(e) || e is System.Printing.PrintSystemException or System.ComponentModel.Win32Exception)
        {
            ShowResult("Printing failed", e.Message + " The transaction is saved; reprint it from History.", true);
        }
    }

    [RelayCommand]
    private async Task PrintLastAsync()
    {
        if (LastTransactionId is { } id) await PrintAsync(id);
    }

    /// <summary>Waits until the indicator reports a stable reading. Snapshot is null on timeout or when the operator cancels.</summary>
    private async Task<(IndicatorSnapshot? Snapshot, bool Cancelled)> WaitForStableAsync(int timeoutMs)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(1000, timeoutMs)));
        using var wait = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, wait.Token);
        _stableWait = wait;
        ShowResult(Loc.T("status.waitStable", "Waiting for stable weight"), "The weight is captured as soon as it is stable. Press Esc to cancel.", false);
        try
        {
            while (true)
            {
                var snap = App.Host.Indicator.Snapshot;
                if (snap.Stability.IsStable && snap.Latest is not null) return (snap, false);
                await Task.Delay(100, linked.Token);
            }
        }
        catch (OperationCanceledException)
        {
            return (null, wait.IsCancellationRequested);
        }
        finally
        {
            _stableWait = null;
        }
    }

    [RelayCommand]
    private void Clear()
    {
        if (_stableWait is { } wait)
        {
            wait.Cancel(); // Esc while waiting for a stable weight cancels the capture, not the details
            return;
        }
        ClearDetails(keepResult: false);
    }

    private void ClearDetails(bool keepResult)
    {
        Vehicle = string.Empty;
        Customer = Customers.FirstOrDefault();
        Supplier = Suppliers.FirstOrDefault();
        Material = Materials.FirstOrDefault();
        Driver = DeliveryNote = Reference = Remarks = string.Empty;
        UseStoredTare = false;
        StageText = ContextText = string.Empty;
        if (!keepResult) ShowResult(string.Empty, string.Empty, false);
    }

    private void ShowResult(string title, string text, bool error)
    {
        ResultTitle = title;
        ResultText = text;
        ResultIsError = error;
    }

    public void SelectPending(TransactionEntity t) => Vehicle = t.VehicleReg;

    // ------------------------------------------------------------------ automation

    [RelayCommand]
    private Task ReleaseVehicleAsync() => Dialogs.RunAsync(async () =>
    {
        if (App.Host.Automation is { } a) await a.ReleaseVehicleAsync(User);
    });

    [RelayCommand]
    private Task AcknowledgeFaultAsync() => Dialogs.RunAsync(async () =>
    {
        if (App.Host.Automation is { } a) await a.AcknowledgeFaultAsync(User);
    });

    private void OnAutomationStatus(object? sender, AutomationStatus s) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
    {
        HasAutomation = App.Host.Hardware is not null || s.Mode == OperatingMode.Automatic;
        AutomationText = $"{s.State}{(s.Vehicle is { } v ? $" · {v.Registration}" : string.Empty)}{(s.LastEvent is { } e ? $" · {e}" : string.Empty)}";
        HasFault = s.Fault != FaultKind.None;
        if (HasFault) AutomationText = $"FAULT: {s.FaultMessage} · {AutomationText}";
        (EntryLightText, EntryLightBrush) = Light(s.EntryLight);
        (ExitLightText, ExitLightBrush) = Light(s.ExitLight);
        BarrierText = s.ExitOpen ? "OPEN" : "CLOSED";
    });

    private static (string, Brush) Light(LightColor c) => c switch
    {
        LightColor.Green => ("GREEN – go", StateBrushes.For(DisplayColor.Green)),
        LightColor.Red => ("RED – stop", StateBrushes.For(DisplayColor.Red)),
        LightColor.Amber => ("AMBER – wait", StateBrushes.For(DisplayColor.Yellow)),
        _ => ("OFF", StateBrushes.For(DisplayColor.Grey)),
    };

    private void OnAutoCaptured(object? sender, CaptureResult result) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(async () =>
    {
        if (result.Success && result.Transaction is { } t)
        {
            _capturedAt = App.Host.Time.GetUtcNow();
            LastTransactionId = t.Id;
            ShowResult($"Automatic: ticket {t.TicketNo} – {(result.IsFinal ? "completed" : "first weighing")}",
                result.IsFinal ? $"{Loc.T("w.net", "Net")}: {_display.Format(t.Net!.Value)}" : $"{Loc.T("w.first", "First weighing")}: {_display.Format(t.First!.Value)}", false);
        }
        else
        {
            ShowResult("Automatic capture refused", result.Message, true);
        }
        await RefreshListsAsync();
    });

    private void OnPrintRequested(object? sender, PrintRequest request) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(async () =>
    {
        if (App.Host.Session.User is null) return; // printed later from History when nobody is signed in
        await MaybePrintAsync(request.TransactionId, request.Final, automatic: true);
    });

    private static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public void Dispose()
    {
        _timer.Stop();
        if (App.Host.Automation is { } a)
        {
            a.AutoCaptured -= OnAutoCaptured;
            a.PrintRequested -= OnPrintRequested;
            a.StatusChanged -= OnAutomationStatus;
        }
    }
}

internal static class LogExtensions
{
    public static void LogInformationSafe(this Microsoft.Extensions.Logging.ILogger log, string message) =>
        Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(log, "{Message}", message);
}
