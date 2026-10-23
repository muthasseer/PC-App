using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Weighbridge.Application;
using Weighbridge.Application.Settings;
using Weighbridge.Core.Security;
using Weighbridge.Data;
using Weighbridge.Desktop.Infrastructure;
using Weighbridge.Desktop.Windows;

namespace Weighbridge.Desktop.Views;

/// <summary>Vehicles, customers, suppliers, materials, drivers, transport companies and orders.</summary>
public sealed class MasterDataView : UserControl, IPage
{
    private static readonly HashSet<string> CommonHidden = ["Id", "Uid", "CreatedUtc", "UpdatedUtc"];
    private readonly List<Func<Task>> _reloaders = [];

    public MasterDataView()
    {
        var tabs = new TabControl { Margin = new Thickness(16) };
        tabs.Items.Add(Tab(Loc.T("md.vehicles", "Vehicles"), VehiclesTab()));
        tabs.Items.Add(Tab(Loc.T("md.customers", "Customers"), PartyTab(PartyKind.Customer)));
        tabs.Items.Add(Tab(Loc.T("md.suppliers", "Suppliers"), PartyTab(PartyKind.Supplier)));
        tabs.Items.Add(Tab(Loc.T("md.materials", "Materials"), SimpleTab<MaterialEntity>(
            async q => (await Md.SearchMaterialsAsync(User, q, 1, 2000, includeInactive: true)).Items, e => Md.SaveMaterialAsync(User, e),
            ("Code", "Code"), ("Name", "Name"), ("Category", "Category"), ("Grade", "Grade"), ("Unit", "Unit"), ("Active", "IsActive"))));
        tabs.Items.Add(Tab(Loc.T("md.drivers", "Drivers"), SimpleTab<DriverEntity>(
            async q => await Md.DriversAsync(q), e => Md.SaveDriverAsync(User, e),
            ("Name", "Name"), ("Licence", "LicenseNo"), ("NIC", "NicNo"), ("Phone", "Phone"), ("Active", "IsActive"))));
        tabs.Items.Add(Tab(Loc.T("md.transporters", "Transport companies"), SimpleTab<TransportCompanyEntity>(
            async _ => await Md.TransportCompaniesAsync(), e => Md.SaveTransportCompanyAsync(User, e),
            ("Code", "Code"), ("Name", "Name"), ("Contact", "ContactPerson"), ("Phone", "Phone"), ("Active", "IsActive"))));
        tabs.Items.Add(Tab(Loc.T("md.orders", "Orders"), OrdersTab()));
        Content = new DockPanel { Children = { Header(), tabs } };
    }

    private static MasterDataService Md => App.Host.MasterData;
    private static UserContext User => App.Host.Session.Require();
    private static bool CanEdit => User.Has(Permissions.MasterDataEdit);

    public async void OnShown()
    {
        foreach (var r in _reloaders) await Dialogs.RunAsync(r);
    }

    public void OnHidden() { }

    private static UIElement Header()
    {
        var t = new TextBlock { Text = Loc.T("nav.master", "Master data"), Style = (Style)System.Windows.Application.Current.FindResource("Text.H1"), Margin = new Thickness(16, 16, 16, 0) };
        DockPanel.SetDock(t, Dock.Top);
        return t;
    }

    private static TabItem Tab(string header, UIElement content) => new() { Header = header, Content = new Border { Padding = new Thickness(10), Child = content } };

    /// <summary>A searchable grid with Add/Edit, editing the entity with a generated form.</summary>
    private UIElement SimpleTab<T>(Func<string?, Task<IReadOnlyList<T>>> load, Func<T, Task<long>> save, params (string Header, string Path)[] columns) where T : class, new()
    {
        var grid = Grid(columns);
        var search = new TextBox { Width = 240 };
        async Task Reload() => grid.ItemsSource = await load(string.IsNullOrWhiteSpace(search.Text) ? null : search.Text.Trim());
        _reloaders.Add(Reload);
        async Task Edit(T? item)
        {
            if (!CanEdit) return;
            var editor = new RecordEditor(item ?? new T(), CommonHidden.Concat(["TransportCompanyId"]).ToHashSet());
            if (!EditorDialog(typeof(T).Name.Replace("Entity", string.Empty, StringComparison.Ordinal), editor)) return;
            await Dialogs.RunAsync(async () =>
            {
                await save(editor.Build<T>());
                await Reload();
            });
        }
        grid.MouseDoubleClick += async (_, _) => { if (grid.SelectedItem is T t) await Edit(t); };
        return Layout(search, grid, Reload, () => Edit(null), () => grid.SelectedItem is T t ? Edit(t) : Task.CompletedTask);
    }

    private UIElement PartyTab(PartyKind kind) => SimpleTab<PartyEntity>(
        async q => (await Md.SearchPartiesAsync(User, kind, q, 1, 2000, includeInactive: true)).Items, e => Md.SavePartyAsync(User, kind, e),
        ("Code", "Code"), ("Name", "Name"), ("Phone", "Phone"), ("Email", "Email"), ("Tax no", "TaxNo"), ("Active", "IsActive"));

    // ------------------------------------------------------------------ vehicles

    private UIElement VehiclesTab()
    {
        var grid = Grid(("Registration", "Registration"), ("Category", "CategoryName"), ("Type", "VehicleType"), ("Make / model", "MakeModel"),
            ("RFID", "RfidTag"), ("Stored tare (g)", "StoredTareG"), ("Tare expires", "StoredTareExpiresUtc"), ("Blacklisted", "IsBlacklisted"), ("Reason", "BlacklistReason"), ("Active", "IsActive"));
        var search = new TextBox { Width = 240 };
        async Task Reload() => grid.ItemsSource = (await Md.SearchVehiclesAsync(User, string.IsNullOrWhiteSpace(search.Text) ? null : search.Text.Trim(), 1, 2000, includeInactive: true)).Items;
        _reloaders.Add(Reload);

        async Task Edit(VehicleEntity? v)
        {
            if (!CanEdit) return;
            var categories = await Md.CategoriesAsync();
            var transporters = await Md.TransportCompaniesAsync();
            v ??= new VehicleEntity();
            var d = new FormDialog(v.Id == 0 ? "New vehicle" : $"Vehicle {v.Registration}");
            var reg = d.AddText(Loc.T("w.vehicle", "Vehicle number"), v.Registration);
            var cat = d.AddCombo("Category", categories, categories.FirstOrDefault(c => c.Id == v.CategoryId), "Name");
            var type = d.AddText("Type", v.VehicleType ?? string.Empty);
            var make = d.AddText("Make / model", v.MakeModel ?? string.Empty);
            var axles = d.AddText("Axles", v.Axles?.ToString(CultureInfo.CurrentCulture) ?? string.Empty);
            var tc = d.AddCombo(Loc.T("md.transporters", "Transport company"), transporters, transporters.FirstOrDefault(t => t.Id == v.TransportCompanyId), "Name");
            var rfid = d.AddText("RFID tag", v.RfidTag ?? string.Empty);
            var qr = d.AddText("QR code", v.QrCode ?? string.Empty);
            var notes = d.AddText(Loc.T("common.remarks", "Notes"), v.Notes ?? string.Empty, multiline: true);
            var active = d.AddCheck("Active", v.IsActive);
            d.Validate = () => int.TryParse(axles.Text, out _) || string.IsNullOrWhiteSpace(axles.Text) ? null : "Axles must be a whole number.";
            if (!d.ShowModal()) return;
            v.Registration = reg.Text.Trim();
            v.CategoryId = (cat.SelectedItem as VehicleCategoryEntity)?.Id;
            v.VehicleType = Blank(type.Text);
            v.MakeModel = Blank(make.Text);
            v.Axles = int.TryParse(axles.Text, out var a) ? a : null;
            v.TransportCompanyId = (tc.SelectedItem as TransportCompanyEntity)?.Id;
            v.RfidTag = Blank(rfid.Text);
            v.QrCode = Blank(qr.Text);
            v.Notes = Blank(notes.Text);
            v.IsActive = active.IsChecked == true;
            await Dialogs.RunAsync(async () =>
            {
                await Md.SaveVehicleAsync(User, v);
                await Reload();
            });
        }

        async Task Blacklist()
        {
            if (grid.SelectedItem is not VehicleEntity v) return;
            if (v.IsBlacklisted)
            {
                if (!Dialogs.Confirm($"Remove {v.Registration} from the blacklist?")) return;
                await Dialogs.RunAsync(() => Md.SetBlacklistAsync(User, v.Id, false, null));
            }
            else
            {
                var reason = Dialogs.Prompt(Loc.T("md.blacklist", "Blacklist"), $"{v.Registration} will be refused at the weighbridge until it is removed from the blacklist.", minLength: 3);
                if (reason is null) return;
                await Dialogs.RunAsync(() => Md.SetBlacklistAsync(User, v.Id, true, reason));
            }
            await Reload();
        }

        async Task StoredTare()
        {
            if (grid.SelectedItem is not VehicleEntity v) return;
            var weighing = await App.Host.Settings.GetAsync<WeighingSettings>(SettingKeys.Weighing);
            var d = new FormDialog($"Stored tare – {v.Registration}", "A stored tare lets the vehicle be weighed once (loaded) using its known empty weight. It must be approved before it can be used.");
            var kg = d.AddText("Tare (kg)", v.StoredTareG is { } g ? (g / 1000m).ToString("0.###", CultureInfo.CurrentCulture) : string.Empty);
            var days = d.AddText("Valid for (days)", weighing.StoredTareValidityDays.ToString(CultureInfo.CurrentCulture));
            d.Validate = () => decimal.TryParse(kg.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var k) && k > 0 && int.TryParse(days.Text, out var dd) && dd > 0
                ? null : "Enter the tare in kilograms and the validity in days.";
            if (!d.ShowModal()) return;
            await Dialogs.RunAsync(() => Md.SetStoredTareAsync(User, v.Id, decimal.Parse(kg.Text, NumberStyles.Number, CultureInfo.CurrentCulture), "Manual entry", null, int.Parse(days.Text, CultureInfo.CurrentCulture)));
            await Reload();
        }

        async Task ApproveTare()
        {
            if (grid.SelectedItem is not VehicleEntity v) return;
            var weighing = await App.Host.Settings.GetAsync<WeighingSettings>(SettingKeys.Weighing);
            if (!Dialogs.Confirm($"Approve the stored tare of {v.Registration} ({(v.StoredTareG ?? 0) / 1000m:#,0.###} kg) for {weighing.StoredTareValidityDays} days?")) return;
            await Dialogs.RunAsync(() => Md.ApproveStoredTareAsync(User, v.Id, weighing.StoredTareValidityDays));
            await Reload();
        }

        grid.MouseDoubleClick += async (_, _) => { if (grid.SelectedItem is VehicleEntity v) await Edit(v); };
        var extra = new List<Button>();
        if (User.Has(Permissions.VehicleBlacklist)) extra.Add(Btn(Loc.T("md.blacklist", "Blacklist"), Blacklist));
        if (CanEdit) extra.Add(Btn("Stored tare…", StoredTare));
        if (User.Has(Permissions.VehicleTareApprove)) extra.Add(Btn(Loc.T("md.approveTare", "Approve stored tare"), ApproveTare));
        return Layout(search, grid, Reload, () => Edit(null), () => grid.SelectedItem is VehicleEntity v ? Edit(v) : Task.CompletedTask, extra.ToArray());
    }

    // ------------------------------------------------------------------ orders

    private UIElement OrdersTab()
    {
        var grid = Grid(("Order no", "OrderNo"), ("Type", "OrderType"), ("Status", "Status"), ("Ordered (g)", "OrderedQtyG"), ("Valid from", "ValidFromUtc"), ("Valid to", "ValidToUtc"), ("Notes", "Notes"));
        var search = new TextBox { Width = 240, IsEnabled = false };
        async Task Reload() => grid.ItemsSource = await Md.OpenOrdersAsync();
        _reloaders.Add(Reload);
        async Task Edit(OrderEntity? o)
        {
            if (!CanEdit) return;
            o ??= new OrderEntity();
            var customers = (await Md.SearchPartiesAsync(User, PartyKind.Customer, null, 1, 2000)).Items;
            var suppliers = (await Md.SearchPartiesAsync(User, PartyKind.Supplier, null, 1, 2000)).Items;
            var materials = (await Md.SearchMaterialsAsync(User, null, 1, 2000)).Items;
            var d = new FormDialog(o.Id == 0 ? "New order" : $"Order {o.OrderNo}");
            var no = d.AddText("Order number", o.OrderNo);
            var type = d.AddCombo("Type", new[] { "Sales", "Purchase" }, o.OrderType);
            var customer = d.AddCombo(Loc.T("w.customer", "Customer"), customers, customers.FirstOrDefault(c => c.Id == o.CustomerId), "Name");
            var supplier = d.AddCombo(Loc.T("w.supplier", "Supplier"), suppliers, suppliers.FirstOrDefault(c => c.Id == o.SupplierId), "Name");
            var material = d.AddCombo(Loc.T("w.material", "Material"), materials, materials.FirstOrDefault(m => m.Id == o.MaterialId), "Name");
            var qty = d.AddText("Ordered quantity (kg)", o.OrderedQtyG is { } q ? (q / 1000m).ToString("0.###", CultureInfo.CurrentCulture) : string.Empty);
            var status = d.AddCombo(Loc.T("common.status", "Status"), new[] { "Open", "Closed", "Cancelled" }, o.Status);
            var notes = d.AddText(Loc.T("common.remarks", "Notes"), o.Notes ?? string.Empty, multiline: true);
            d.Validate = () => string.IsNullOrWhiteSpace(no.Text) ? "Enter the order number." :
                !string.IsNullOrWhiteSpace(qty.Text) && !decimal.TryParse(qty.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out _) ? "The quantity must be a number." : null;
            if (!d.ShowModal()) return;
            o.OrderNo = no.Text.Trim();
            o.OrderType = (string)type.SelectedItem!;
            o.CustomerId = (customer.SelectedItem as PartyEntity)?.Id;
            o.SupplierId = (supplier.SelectedItem as PartyEntity)?.Id;
            o.MaterialId = (material.SelectedItem as MaterialEntity)?.Id;
            o.OrderedQtyG = decimal.TryParse(qty.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var kg) ? (long)(kg * 1000) : null;
            o.Status = (string)status.SelectedItem!;
            o.Notes = Blank(notes.Text);
            await Dialogs.RunAsync(async () =>
            {
                await Md.SaveOrderAsync(User, o);
                await Reload();
            });
        }
        grid.MouseDoubleClick += async (_, _) => { if (grid.SelectedItem is OrderEntity o) await Edit(o); };
        return Layout(search, grid, Reload, () => Edit(null), () => grid.SelectedItem is OrderEntity o ? Edit(o) : Task.CompletedTask);
    }

    // ------------------------------------------------------------------ helpers

    private static DataGrid Grid(params (string Header, string Path)[] columns)
    {
        var grid = new DataGrid();
        foreach (var (header, path) in columns)
        {
            grid.Columns.Add(path.StartsWith("Is", StringComparison.Ordinal)
                ? new DataGridCheckBoxColumn { Header = header, Binding = new Binding(path) }
                : new DataGridTextColumn
                {
                    Header = header,
                    Binding = path.EndsWith("Utc", StringComparison.Ordinal) ? new Binding(path) { Converter = new LocalTimeConverter(), ConverterParameter = "yyyy-MM-dd" }
                        : path.EndsWith("G", StringComparison.Ordinal) && path != "Tag" ? new Binding(path) { Converter = new GramsConverter() }
                        : new Binding(path),
                    Width = path is "Name" or "Notes" or "BlacklistReason" ? new DataGridLength(1, DataGridLengthUnitType.Star) : DataGridLength.Auto,
                });
        }
        return grid;
    }

    private static UIElement Layout(TextBox search, DataGrid grid, Func<Task> reload, Func<Task> add, Func<Task> edit, params Button[] extra)
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        bar.Children.Add(search);
        System.Windows.Automation.AutomationProperties.SetName(search, Loc.T("common.search", "Search"));
        search.KeyDown += async (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) await Dialogs.RunAsync(reload); };
        bar.Children.Add(Btn(Loc.T("common.search", "Search"), reload, margin: 8));
        if (CanEdit)
        {
            bar.Children.Add(Btn(Loc.T("common.add", "Add"), add, primary: true));
            bar.Children.Add(Btn(Loc.T("common.edit", "Edit"), edit));
        }
        foreach (var b in extra) bar.Children.Add(b);
        DockPanel.SetDock(bar, Dock.Top);
        return new DockPanel { Children = { bar, grid } };
    }

    private static Button Btn(string text, Func<Task> action, bool primary = false, double margin = 0)
    {
        var b = new Button { Content = text, Margin = new Thickness(margin, 0, 8, 0) };
        if (primary) b.Style = (Style)System.Windows.Application.Current.FindResource("Button.Primary");
        b.Click += async (_, _) => await Dialogs.RunAsync(action);
        return b;
    }

    private static bool EditorDialog(string title, RecordEditor editor)
    {
        var d = new FormDialog(title);
        d.Add(string.Empty, new ScrollViewer { Content = editor, MaxHeight = 520, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        d.Validate = () =>
        {
            try { editor.Build(); return null; }
            catch (FormatException e) { return e.Message; }
        };
        return d.ShowModal();
    }

    private static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
