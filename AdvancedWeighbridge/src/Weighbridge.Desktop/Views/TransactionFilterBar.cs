using System.Windows;
using System.Windows.Controls;
using Weighbridge.Core.Weighing;
using Weighbridge.Data;
using Weighbridge.Data.Stores;
using Weighbridge.Desktop.Infrastructure;

namespace Weighbridge.Desktop.Views;

/// <summary>Search fields shared by History and Reports; produces a <see cref="TransactionFilter"/>.</summary>
public sealed class TransactionFilterBar : WrapPanel
{
    private readonly DatePicker _from = new() { Width = 130 };
    private readonly DatePicker _to = new() { Width = 130 };
    private readonly ComboBox _dateField = new() { Width = 120, ItemsSource = new[] { "Created", "Completed" }, SelectedIndex = 0 };
    private readonly TextBox _text = new() { Width = 200 };
    private readonly TextBox _vehicle = new() { Width = 130 };
    private readonly ComboBox _status = new() { Width = 130 };
    private readonly ComboBox _mode = new() { Width = 140 };
    private readonly ComboBox _customer = new() { Width = 180, DisplayMemberPath = "Name" };
    private readonly ComboBox _supplier = new() { Width = 180, DisplayMemberPath = "Name" };
    private readonly ComboBox _material = new() { Width = 160, DisplayMemberPath = "Name" };
    private readonly CheckBox _manual = new() { Content = "Manual weights only", VerticalAlignment = VerticalAlignment.Bottom };

    public TransactionFilterBar()
    {
        Margin = new Thickness(0, 0, 0, 8);
        _from.SelectedDate = DateTime.Today.AddDays(-7);
        _to.SelectedDate = DateTime.Today;
        _status.ItemsSource = new object[] { Loc.T("common.all", "All") }.Concat(Enum.GetValues<TransactionStatus>().Cast<object>()).ToList();
        _status.SelectedIndex = 0;
        _mode.ItemsSource = new object[] { Loc.T("common.all", "All") }.Concat(Enum.GetValues<OperatingMode>().Cast<object>()).ToList();
        _mode.SelectedIndex = 0;
        Add(Loc.T("common.from", "From"), _from);
        Add(Loc.T("common.to", "To"), _to);
        Add("Date of", _dateField);
        Add(Loc.T("common.search", "Search") + " (ticket, vehicle, names)", _text);
        Add(Loc.T("w.vehicle", "Vehicle"), _vehicle);
        Add(Loc.T("common.status", "Status"), _status);
        Add(Loc.T("w.mode", "Mode"), _mode);
        Add(Loc.T("w.customer", "Customer"), _customer);
        Add(Loc.T("w.supplier", "Supplier"), _supplier);
        Add(Loc.T("w.material", "Material"), _material);
        Children.Add(new Border { Child = _manual, Margin = new Thickness(0, 0, 12, 6), VerticalAlignment = VerticalAlignment.Bottom });
        Loaded += async (_, _) => await LoadListsAsync();
    }

    private bool _listsLoaded;

    private async Task LoadListsAsync()
    {
        if (_listsLoaded || App.Host.Session.User is not { } user) return;
        _listsLoaded = true;
        var md = App.Host.MasterData;
        _customer.ItemsSource = new PartyEntity[] { new() { Name = "—" } }.Concat((await md.SearchPartiesAsync(user, PartyKind.Customer, null, 1, 2000)).Items).ToList();
        _supplier.ItemsSource = new PartyEntity[] { new() { Name = "—" } }.Concat((await md.SearchPartiesAsync(user, PartyKind.Supplier, null, 1, 2000)).Items).ToList();
        _material.ItemsSource = new MaterialEntity[] { new() { Name = "—" } }.Concat((await md.SearchMaterialsAsync(user, null, 1, 2000)).Items).ToList();
        _customer.SelectedIndex = _supplier.SelectedIndex = _material.SelectedIndex = 0;
    }

    private void Add(string label, FrameworkElement control)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 12, 6) };
        panel.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("Text.Label") });
        panel.Children.Add(control);
        System.Windows.Automation.AutomationProperties.SetName(control, label);
        Children.Add(panel);
    }

    public TransactionFilter Build(int page = 1, int pageSize = 100) => new()
    {
        From = _from.SelectedDate is { } f ? new DateTimeOffset(f.Date) : null,
        To = _to.SelectedDate is { } t ? new DateTimeOffset(t.Date.AddDays(1)) : null,
        DateField = _dateField.SelectedIndex == 1 ? TransactionDateField.Completed : TransactionDateField.Created,
        Text = string.IsNullOrWhiteSpace(_text.Text) ? null : _text.Text.Trim(),
        Vehicle = string.IsNullOrWhiteSpace(_vehicle.Text) ? null : _vehicle.Text.Trim(),
        Status = _status.SelectedItem as TransactionStatus?,
        Mode = _mode.SelectedItem as OperatingMode?,
        CustomerId = (_customer.SelectedItem as PartyEntity) is { Id: > 0 } c ? c.Id : null,
        SupplierId = (_supplier.SelectedItem as PartyEntity) is { Id: > 0 } s ? s.Id : null,
        MaterialId = (_material.SelectedItem as MaterialEntity) is { Id: > 0 } m ? m.Id : null,
        HasManualWeight = _manual.IsChecked == true ? true : null,
        Page = page,
        PageSize = pageSize,
    };
}
