using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Weighbridge.Application.History;
using Weighbridge.Application.Security;
using Weighbridge.Application.Settings;
using Weighbridge.Application.Weighing;
using Weighbridge.Core.Security;
using Weighbridge.Core.Units;
using Weighbridge.Core.Weighing;
using Weighbridge.Data;
using Weighbridge.Desktop.Infrastructure;

namespace Weighbridge.Desktop.Windows;

/// <summary>Complete record of one transaction: details, every measurement with its raw indicator frames, images, timeline and audit.</summary>
public partial class TransactionWindow : Window
{
    private readonly long _id;
    private TransactionDetail? _detail;

    public TransactionWindow(long transactionId)
    {
        InitializeComponent();
        _id = transactionId;
        Owner = Dialogs.Owner;
        Loaded += async (_, _) => await Dialogs.RunAsync(LoadAsync);
    }

    private static UserContext User => App.Host.Session.Require();
    private static WeightDisplayOptions Display => GramsConverter.Display;

    private async Task LoadAsync()
    {
        var d = _detail = await App.Host.History.DetailAsync(User, _id);
        var t = d.Transaction;
        Title = $"Ticket {t.TicketNo}";
        TitleText.Text = $"Ticket {t.TicketNo} · {t.VehicleReg} · {Loc.T("tx." + t.Status, t.Status.ToString())}";
        SubtitleText.Text = $"{t.SiteCode}/{t.WeighbridgeCode} · {t.OperatingMode} · created {t.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm} by {t.CreatedByName}";
        var manual = d.Measurements.Where(m => m.Source == MeasurementSource.Manual).ToList();
        var simulated = d.Measurements.Any(m => m.PortDescription == "Simulator");
        if (manual.Count > 0 || t.HasOverride || simulated)
        {
            ManualBanner.Visibility = Visibility.Visible;
            ManualText.Text = string.Join("  ", new[]
            {
                manual.Count > 0 ? $"Contains a MANUAL weight (not an indicator measurement): {string.Join("; ", manual.Select(m => m.ManualReason))}" : null,
                t.HasOverride ? "An unstable-weight override was used." : null,
                simulated ? "SIMULATOR weights – training data, not a real weighing." : null,
            }.Where(x => x is not null));
        }

        DetailsGrid.Children.Clear();
        DetailsGrid.RowDefinitions.Clear();
        var rows = new (string, string?)[]
        {
            ("Ticket", t.TicketNo), ("Status", t.Status.ToString()), ("Vehicle", t.VehicleReg), ("Direction", t.Direction.ToString()),
            ("Customer", t.CustomerName), ("Supplier", t.SupplierName), ("Material", t.MaterialName), ("Driver", t.DriverName),
            ("Delivery note", t.DeliveryNoteNo), ("Reference", t.ReferenceNo), ("Remarks", t.Remarks),
            ("First weight", t.First is { } f ? Display.Format(f) : null), ("Second weight", t.Second is { } s ? Display.Format(s) : null),
            ("Gross", t.Gross is { } g ? Display.Format(g) : null), ("Tare", t.Tare is { } ta ? $"{Display.Format(ta)} ({t.TareMethod})" : null),
            ("Net", t.Net is { } n ? Display.Format(n) : null), ("Mode", t.OperatingMode.ToString()),
            ("Completed", t.CompletedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
            ("Cancelled", t.CancelledUtc is { } c ? $"{c.ToLocalTime():yyyy-MM-dd HH:mm} – {t.CancelReason}" : null),
            ("Replaces ticket", d.Replaces?.TicketNo), ("Replaced by ticket", d.ReplacedBy?.TicketNo),
            ("Printed", d.Prints.Count == 0 ? "not printed" : $"{d.Prints.Count} time(s), last {d.Prints[^1].AtUtc.ToLocalTime():yyyy-MM-dd HH:mm}"),
            ("Record id", $"{t.Uid} (version {t.Version})"),
        };
        foreach (var (label, value) in rows.Where(r => !string.IsNullOrEmpty(r.Item2)))
        {
            DetailsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = label, Style = (Style)FindResource("Text.Label"), Margin = new Thickness(0, 4, 8, 4) };
            var v = new TextBox { Text = value, IsReadOnly = true, BorderThickness = new Thickness(0), Background = null, Margin = new Thickness(0, 2, 0, 2), FontWeight = label == "Net" ? FontWeights.Bold : FontWeights.Normal };
            Grid.SetRow(l, DetailsGrid.RowDefinitions.Count - 1);
            Grid.SetRow(v, DetailsGrid.RowDefinitions.Count - 1);
            Grid.SetColumn(v, 1);
            DetailsGrid.Children.Add(l);
            DetailsGrid.Children.Add(v);
        }

        MeasurementsGrid.ItemsSource = d.Measurements;
        if (d.Measurements.Count > 0) MeasurementsGrid.SelectedIndex = 0;
        TimelineGrid.ItemsSource = d.Timeline;
        AuditGrid.ItemsSource = d.Audit;
        await LoadImagesAsync(d);

        var user = User;
        var open = t.Status != TransactionStatus.Cancelled;
        PrintButton.IsEnabled = user.Has(Permissions.TicketPrint);
        CancelButton.IsEnabled = open && user.Has(Permissions.TransactionCancel);
        EditButton.IsEnabled = open && user.Has(Permissions.TransactionEdit);
        CorrectButton.Visibility = t.Status == TransactionStatus.Pending && user.Has(Permissions.TransactionCorrectWeight) ? Visibility.Visible : Visibility.Collapsed;
        ReissueButton.Visibility = t.Status == TransactionStatus.Completed && user.Has(Permissions.TransactionCorrectWeight) && user.Has(Permissions.TransactionCancel)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task LoadImagesAsync(TransactionDetail d)
    {
        ImagesPanel.Children.Clear();
        if (d.Evidence.Count == 0) ImagesPanel.Children.Add(new TextBlock { Text = "No images were captured for this transaction.", Style = (Style)FindResource("Text.Muted") });
        foreach (var e in d.Evidence)
        {
            var file = await App.Host.History.ReadEvidenceAsync(e);
            var panel = new StackPanel { Margin = new Thickness(0, 0, 12, 12), Width = 420 };
            if (file.Data is { } bytes)
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = new MemoryStream(bytes);
                image.EndInit();
                panel.Children.Add(new Image { Source = image, Stretch = System.Windows.Media.Stretch.Uniform, MaxHeight = 300 });
            }
            panel.Children.Add(new TextBlock { Text = $"{e.CameraName} · {e.CapturedUtc.ToLocalTime():HH:mm:ss}", FontWeight = FontWeights.SemiBold });
            panel.Children.Add(new TextBlock { Text = file.Message, Style = (Style)FindResource(file.IntegrityOk ? "Text.Muted" : "Text.Error") });
            ImagesPanel.Children.Add(panel);
        }
    }

    private void OnMeasurementSelected(object sender, SelectionChangedEventArgs e)
    {
        if (MeasurementsGrid.SelectedItem is not MeasurementEntity m || _detail is null) return;
        var lines = new List<string>
        {
            $"Measurement {m.Uid} · {TicketService.SourceLabel(m)} · {Display.Format(m.Weight)}",
            $"Reported value '{m.ReportedValue}' {m.ReportedUnit} · flags 0x{m.IndicatorFlags:X} · stability {m.StabilityReason} span {m.StabilitySpanG / 1000m:0.###} kg for {m.StableForMs} ms",
        };
        if (m.RawText is not null) lines.Add($"Captured frame (text): {m.RawText}");
        if (m.RawHex is not null) lines.Add($"Captured frame (hex):  {m.RawHex}");
        if (m.CorrectionReason is not null) lines.Add($"Correction of an earlier measurement: {m.CorrectionReason}");
        lines.Add(string.Empty);
        if (_detail.RawFrames.TryGetValue(m.Id, out var frames) && frames.Count > 0)
        {
            lines.Add("Frames received around the capture (* = captured):");
            lines.AddRange(frames.Select(f => (f.IsCaptured ? "* " : "  ") + HistoryService.FrameSummary(f) + "   " + f.Hex));
        }
        else
        {
            lines.Add(m.Source == MeasurementSource.Manual ? "Manual entry – no indicator data." : "No raw frames stored.");
        }
        FramesText.Text = string.Join(Environment.NewLine, lines);
    }

    private async void OnPrint(object sender, RoutedEventArgs e)
    {
        string? reason = null;
        if (_detail!.Prints.Count > 0)
        {
            reason = Dialogs.Prompt(Loc.T("tx.reprint", "Reprint"), "This ticket was printed before. The copy is marked as a reprint and the reason is recorded.", minLength: 3);
            if (reason is null) return;
        }
        await Dialogs.RunAsync(async () =>
        {
            await App.Host.Tickets.PrintAsync(User, _id, new WpfPrintService(), reason);
            await LoadAsync();
        });
    }

    private async void OnPdf(object sender, RoutedEventArgs e)
    {
        var path = Dialogs.SaveFile("PDF (*.pdf)|*.pdf", $"{_detail!.Transaction.TicketNo}.pdf");
        if (path is null) return;
        await Dialogs.RunAsync(async () =>
        {
            await App.Host.Tickets.ExportPdfAsync(User, _id, path);
            Dialogs.Info($"Saved {path}");
        });
    }

    private async void OnCancelTransaction(object sender, RoutedEventArgs e)
    {
        var approvals = await App.Host.Settings.GetAsync<ApprovalSettings>(SettingKeys.Approvals);
        var reason = Dialogs.Prompt(Loc.T("tx.cancel", "Cancel transaction"), $"Ticket {_detail!.Transaction.TicketNo} will be marked as cancelled. The record and its weights are kept.", minLength: approvals.MinimumReasonLength, multiline: true);
        if (reason is null) return;
        await WithApprovalAsync("Cancel ticket " + _detail.Transaction.TicketNo, Permissions.TransactionCancel, approval => App.Host.Weighing.CancelAsync(User, _id, reason, approval));
    }

    private async void OnEdit(object sender, RoutedEventArgs e)
    {
        var t = _detail!.Transaction;
        var md = App.Host.MasterData;
        var customers = (await md.SearchPartiesAsync(User, PartyKind.Customer, null, 1, 1000)).Items;
        var suppliers = (await md.SearchPartiesAsync(User, PartyKind.Supplier, null, 1, 1000)).Items;
        var materials = (await md.SearchMaterialsAsync(User, null, 1, 1000)).Items;
        var d = new FormDialog($"Edit ticket {t.TicketNo}", t.Status == TransactionStatus.Completed ? "Changes to a completed ticket need a reason and are recorded with the old and new values." : null);
        var vehicle = d.AddText(Loc.T("w.vehicle", "Vehicle number"), t.VehicleReg);
        vehicle.IsEnabled = t.Status == TransactionStatus.Pending;
        var customer = d.AddCombo(Loc.T("w.customer", "Customer"), customers, customers.FirstOrDefault(c => c.Id == t.CustomerId), "Name");
        var supplier = d.AddCombo(Loc.T("w.supplier", "Supplier"), suppliers, suppliers.FirstOrDefault(c => c.Id == t.SupplierId), "Name");
        var material = d.AddCombo(Loc.T("w.material", "Material"), materials, materials.FirstOrDefault(c => c.Id == t.MaterialId), "Name");
        var driver = d.AddText(Loc.T("w.driver", "Driver"), t.DriverName ?? string.Empty);
        var note = d.AddText(Loc.T("w.deliveryNote", "Delivery note"), t.DeliveryNoteNo ?? string.Empty);
        var reference = d.AddText(Loc.T("w.reference", "Reference"), t.ReferenceNo ?? string.Empty);
        var remarks = d.AddText(Loc.T("common.remarks", "Remarks"), t.Remarks ?? string.Empty, multiline: true);
        var reason = d.AddText(Loc.T("common.reason", "Reason"), multiline: true);
        if (!d.ShowModal()) return;
        var details = new TransactionDetails
        {
            VehicleReg = vehicle.Text.Trim(), Direction = t.Direction, CustomerId = (customer.SelectedItem as PartyEntity)?.Id, SupplierId = (supplier.SelectedItem as PartyEntity)?.Id,
            MaterialId = (material.SelectedItem as MaterialEntity)?.Id, DriverName = Blank(driver.Text), DeliveryNoteNo = Blank(note.Text), ReferenceNo = Blank(reference.Text),
            Remarks = Blank(remarks.Text), OrderId = t.OrderId,
        };
        await WithApprovalAsync("Edit ticket " + t.TicketNo, Permissions.ApprovalsDecide, approval => App.Host.Weighing.UpdateDetailsAsync(User, _id, details, Blank(reason.Text), approval));
    }

    private async void OnCorrect(object sender, RoutedEventArgs e)
    {
        var approvals = await App.Host.Settings.GetAsync<ApprovalSettings>(SettingKeys.Approvals);
        var d = new FormDialog("Correct the first weight", "The original measurement is kept; the correction is recorded as a new manual measurement with the reason and the supervisor.");
        var weight = d.AddText("Correct first weight (kg)");
        var reason = d.AddText(Loc.T("common.reason", "Reason"), multiline: true);
        d.Validate = () => !decimal.TryParse(weight.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var kg) || kg <= 0 ? "Enter the weight in kilograms."
            : reason.Text.Trim().Length < approvals.MinimumReasonLength ? $"Enter a reason of at least {approvals.MinimumReasonLength} characters." : null;
        if (!d.ShowModal()) return;
        var kgValue = decimal.Parse(weight.Text, NumberStyles.Number, CultureInfo.CurrentCulture);
        var approval = await SupervisorAsync("Correct first weight of " + _detail!.Transaction.TicketNo, Permissions.TransactionCorrectWeight);
        if (approval is null) return;
        await Dialogs.RunAsync(async () =>
        {
            await App.Host.Weighing.CorrectFirstWeightAsync(User, _id, kgValue, reason.Text.Trim(), approval);
            await LoadAsync();
        });
    }

    private async void OnReissue(object sender, RoutedEventArgs e)
    {
        var t = _detail!.Transaction;
        var approvals = await App.Host.Settings.GetAsync<ApprovalSettings>(SettingKeys.Approvals);
        var d = new FormDialog("Cancel and re-issue", $"Ticket {t.TicketNo} is cancelled and a new ticket is issued with the corrected weights. Both remain in the history and refer to each other.");
        var first = d.AddText("First weight (kg) – empty keeps the original", string.Empty);
        var second = d.AddText("Second weight (kg) – empty keeps the original", string.Empty);
        var reason = d.AddText(Loc.T("common.reason", "Reason"), multiline: true);
        d.Validate = () => reason.Text.Trim().Length < approvals.MinimumReasonLength ? $"Enter a reason of at least {approvals.MinimumReasonLength} characters." : null;
        if (!d.ShowModal()) return;
        var approval = await SupervisorAsync("Re-issue ticket " + t.TicketNo, Permissions.TransactionCorrectWeight);
        if (approval is null) return;
        await Dialogs.RunAsync(async () =>
        {
            var replacement = await App.Host.Weighing.ReissueAsync(User, _id, ParseKg(first.Text), ParseKg(second.Text), reason.Text.Trim(), approval);
            Dialogs.Info($"Ticket {replacement.TicketNo} was issued and replaces {t.TicketNo}.");
            new TransactionWindow(replacement.Id).Show();
            Close();
        });
    }

    /// <summary>Runs the action; when the service asks for supervisor authorisation, asks for it and runs again.</summary>
    private async Task WithApprovalAsync(string action, string permission, Func<SupervisorApproval?, Task<TransactionEntity>> work)
    {
        await Dialogs.RunAsync(async () =>
        {
            try
            {
                await work(null);
            }
            catch (ApprovalRequiredException ex)
            {
                var approval = await SupervisorAsync($"{action}: {ex.Message}", permission);
                if (approval is null) return;
                await work(approval);
            }
            await LoadAsync();
        });
    }

    private static async Task<SupervisorApproval?> SupervisorAsync(string action, string permission)
    {
        var sup = Dialogs.Supervisor(action, 0);
        if (sup is null) return null;
        var approval = await App.Host.Auth.VerifySupervisorAsync(sup.Username, sup.Password, permission, action);
        if (approval is null) Dialogs.Error("The supervisor credentials were not accepted, or the supervisor does not have the required permission.");
        return approval;
    }

    private static decimal? ParseKg(string text) => decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var v) ? v : null;

    private static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
