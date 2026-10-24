using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using QRCoder;
using Weighbridge.Application.Maintenance;
using Weighbridge.Application.Reports;
using Weighbridge.Application.Security;
using Weighbridge.Application.Settings;
using Weighbridge.Application.Sync;
using Weighbridge.Core.Security;
using Weighbridge.Data;
using Weighbridge.Data.Stores;
using Weighbridge.Desktop.Infrastructure;
using Weighbridge.Desktop.Windows;

namespace Weighbridge.Desktop.Views;

/// <summary>Helpers shared by the code-built administration pages.</summary>
internal static class Ui
{
    public static Style S(string key) => (Style)System.Windows.Application.Current.FindResource(key);

    public static UserContext User => App.Host.Session.Require();

    public static TextBlock H1(string text) => new() { Text = text, Style = S("Text.H1") };

    public static TextBlock H2(string text) => new() { Text = text, Style = S("Text.H2") };

    public static TextBlock Muted(string text) => new() { Text = text, Style = S("Text.Muted"), Margin = new Thickness(0, 0, 0, 8) };

    public static Button Btn(string text, Func<Task> action, string? style = null)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 8) };
        if (style is not null) b.Style = S(style);
        b.Click += async (_, _) => await Dialogs.RunAsync(action);
        return b;
    }

    public static Border Card(params UIElement[] children)
    {
        var p = new StackPanel();
        foreach (var c in children) p.Children.Add(c);
        return new Border { Style = S("Card"), Child = p };
    }

    public static DataGrid Grid(params (string Header, string Path)[] columns)
    {
        var grid = new DataGrid { MinHeight = 120 };
        foreach (var (header, path) in columns)
        {
            if (path.StartsWith("Is", StringComparison.Ordinal) || path is "MfaEnabled" or "Valid")
            {
                grid.Columns.Add(new DataGridCheckBoxColumn { Header = header, Binding = new Binding(path) { Mode = BindingMode.OneWay } });
                continue;
            }
            var binding = new Binding(path);
            if (path.EndsWith("Utc", StringComparison.Ordinal)) binding.Converter = new LocalTimeConverter();
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = header, Binding = binding,
                Width = path is "Summary" or "Message" or "Reason" or "FilePath" or "Error" ? new DataGridLength(1, DataGridLengthUnitType.Star) : DataGridLength.Auto,
            });
        }
        return grid;
    }

    public static UIElement Page(params UIElement[] children)
    {
        var p = new StackPanel { Margin = new Thickness(16) };
        foreach (var c in children) p.Children.Add(c);
        return new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    public static StackPanel Row(params UIElement[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    public static BitmapImage Qr(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(6);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(png);
        image.EndInit();
        image.Freeze();
        return image;
    }

    public static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }
}

// ====================================================================== users and roles

public sealed class UsersView : UserControl, IPage
{
    private readonly DataGrid _users = Ui.Grid(("User", "Username"), ("Name", "DisplayName"), ("Role", "RoleName"), ("Active", "IsActive"), ("MFA", "MfaEnabled"),
        ("Must change password", "MustChangePassword"), ("Locked until", "LockedUntilUtc"), ("Last sign-in", "LastLoginUtc"));
    private readonly StackPanel _roles = new();

    public UsersView()
    {
        _users.Columns[5] = new DataGridCheckBoxColumn { Header = "Must change password", Binding = new Binding("MustChangePassword") { Mode = BindingMode.OneWay } };
        Content = Ui.Page(
            Ui.H1(Loc.T("nav.users", "Users & roles")),
            Ui.Card(Ui.Row(Ui.Btn(Loc.T("common.add", "Add"), AddAsync, "Button.Primary"), Ui.Btn(Loc.T("common.edit", "Edit"), EditAsync), Ui.Btn("Reset password", ResetAsync),
                Ui.Btn("Remove authenticator", RemoveMfaAsync), Ui.Btn("Set up my authenticator", MyMfaAsync)), _users),
            Ui.Card(Ui.H2("Role permissions"), Ui.Muted("Permissions of each role. The Administrator role always keeps every permission."), _roles));
        _users.MouseDoubleClick += async (_, _) => await Dialogs.RunAsync(EditAsync);
    }

    public async void OnShown() => await Dialogs.RunAsync(LoadAsync);

    public void OnHidden() { }

    private async Task LoadAsync()
    {
        _users.ItemsSource = await App.Host.Auth.UsersAsync(Ui.User);
        await LoadRolesAsync();
    }

    private UserEntity? Selected => _users.SelectedItem as UserEntity;

    private async Task AddAsync()
    {
        var roles = await App.Host.Auth.RolesAsync();
        var d = new FormDialog("New user", "Every person gets an own account; accounts are never shared.");
        var name = d.AddText(Loc.T("login.username", "User name"));
        var display = d.AddText("Full name");
        var role = d.AddCombo("Role", roles.Select(r => r.Name).ToList(), Roles.Operator);
        var pass = d.AddPassword("Temporary password");
        d.AddNote("The user must change the password at the first sign-in.");
        d.Validate = () => string.IsNullOrWhiteSpace(name.Text) || string.IsNullOrWhiteSpace(display.Text) ? "Enter the user name and full name." : null;
        if (!d.ShowModal()) return;
        await App.Host.Auth.CreateUserAsync(Ui.User, name.Text.Trim(), display.Text.Trim(), pass.Password, (string)role.SelectedItem!);
        await LoadAsync();
    }

    private async Task EditAsync()
    {
        if (Selected is not { } u) return;
        var roles = await App.Host.Auth.RolesAsync();
        var d = new FormDialog($"User {u.Username}");
        var display = d.AddText("Full name", u.DisplayName);
        var role = d.AddCombo("Role", roles.Select(r => r.Name).ToList(), u.RoleName);
        var active = d.AddCheck("Active (can sign in)", u.IsActive);
        if (!d.ShowModal()) return;
        await App.Host.Auth.UpdateUserAsync(Ui.User, u.Id, display.Text.Trim(), (string)role.SelectedItem!, active.IsChecked == true);
        await LoadAsync();
    }

    private async Task ResetAsync()
    {
        if (Selected is not { } u) return;
        var d = new FormDialog($"Reset password – {u.Username}", "Sets a temporary password, unlocks the account and requires a new password at the next sign-in.");
        var pass = d.AddPassword("Temporary password");
        if (!d.ShowModal()) return;
        await App.Host.Auth.ResetPasswordAsync(Ui.User, u.Id, pass.Password);
        await LoadAsync();
    }

    private async Task RemoveMfaAsync()
    {
        if (Selected is not { MfaEnabled: true } u || !Dialogs.Confirm($"Remove the authenticator of {u.Username}? They then sign in with the password only.")) return;
        await App.Host.Auth.DisableMfaAsync(Ui.User, u.Id);
        await LoadAsync();
    }

    private async Task MyMfaAsync()
    {
        var (secret, uri) = AuthService.BeginMfaEnrolment(Ui.User, "Advanced Weighbridge");
        var d = new FormDialog("Authenticator app", "Scan the code with an authenticator app (Google Authenticator, Microsoft Authenticator, …), then enter the 6-digit code it shows.");
        d.Add("QR code", new Image { Source = Ui.Qr(uri), Width = 220, Height = 220, HorizontalAlignment = HorizontalAlignment.Left });
        var key = d.AddText("Secret (for manual entry)", secret);
        key.IsReadOnly = true;
        var code = d.AddText("Code from the app");
        if (!d.ShowModal()) return;
        if (await App.Host.Auth.CompleteMfaEnrolmentAsync(Ui.User, secret, code.Text.Trim())) Dialogs.Info("The authenticator is now required when you sign in.");
        else Dialogs.Error("The code does not match. Check the time on your phone and try again.");
        await LoadAsync();
    }

    private async Task LoadRolesAsync()
    {
        _roles.Children.Clear();
        var canEdit = Ui.User.Has(Permissions.RolesManage);
        foreach (var role in await App.Host.Auth.RolesAsync())
        {
            var boxes = Permissions.All.Select(p => new CheckBox { Content = p, IsChecked = role.Permissions.Contains(p), IsEnabled = canEdit && role.Name != Roles.Administrator, Width = 230 }).ToList();
            var wrap = new WrapPanel();
            foreach (var b in boxes) wrap.Children.Add(b);
            var save = Ui.Btn($"Save {role.Name}", async () =>
            {
                await App.Host.Auth.SetRolePermissionsAsync(Ui.User, role.Id, boxes.Where(b => b.IsChecked == true).Select(b => (string)b.Content).ToList());
                Dialogs.Info($"Permissions of {role.Name} saved. They apply at the next sign-in.");
            });
            save.IsEnabled = canEdit && role.Name != Roles.Administrator;
            _roles.Children.Add(new Expander { Header = role.Name, Content = new StackPanel { Children = { wrap, save } }, Margin = new Thickness(0, 4, 0, 4) });
        }
    }
}

// ====================================================================== settings

public sealed class SettingsView : UserControl, IPage
{
    private readonly ContentControl _editor = new();
    private readonly ListBox _sections = new() { Width = 220, Margin = new Thickness(0, 0, 16, 0) };

    private static readonly (string Key, string Title, Type Type)[] Sections =
    [
        (SettingKeys.Company, "Company", typeof(CompanySettings)),
        (SettingKeys.Localization, "Language and display", typeof(LocalizationSettings)),
        (SettingKeys.Weighing, "Weighing rules", typeof(WeighingSettings)),
        (SettingKeys.Approvals, "Approvals", typeof(ApprovalSettings)),
        (SettingKeys.Printing, "Printing and tickets", typeof(PrintingSettings)),
        (SettingKeys.Automation, "Automatic mode", typeof(AutomationSettings)),
        (SettingKeys.Security, "Security", typeof(SecuritySettings)),
        (SettingKeys.Backup, "Backup", typeof(BackupSettings)),
        (SettingKeys.Cloud, "Cloud synchronisation", typeof(CloudSettings)),
        ("weighbridge", "Weighbridge", typeof(object)),
    ];

    public SettingsView()
    {
        _sections.ItemsSource = Sections.Select(s => s.Title).ToList();
        _sections.SelectionChanged += async (_, _) => await Dialogs.RunAsync(ShowAsync);
        DockPanel.SetDock(_sections, Dock.Left);
        var title = Ui.H1(Loc.T("nav.settings", "Settings"));
        DockPanel.SetDock(title, Dock.Top);
        Content = new DockPanel
        {
            Margin = new Thickness(16),
            Children = { title, _sections, new Border { Style = Ui.S("Card"), Child = new ScrollViewer { Content = _editor, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } } },
        };
    }

    public void OnShown()
    {
        if (_sections.SelectedIndex < 0) _sections.SelectedIndex = 0;
    }

    public void OnHidden() { }

    private async Task ShowAsync()
    {
        if (_sections.SelectedIndex < 0) return;
        var (key, title, type) = Sections[_sections.SelectedIndex];
        if (key == "weighbridge")
        {
            _editor.Content = await WeighbridgePanelAsync();
            return;
        }
        var value = await LoadAsync(key, type);
        var hidden = new HashSet<string> { "ActiveWeighbridgeId", "LogoBase64", "Language", "Theme" };
        var editor = new RecordEditor(value, hidden);
        var panel = new StackPanel();
        panel.Children.Add(Ui.H2(title));
        if (type == typeof(LocalizationSettings)) panel.Children.Add(LanguageTheme((LocalizationSettings)value));
        if (type == typeof(CompanySettings)) panel.Children.Add(LogoPanel());
        if (type == typeof(PrintingSettings)) panel.Children.Add(PrinterPanel());
        if (type == typeof(CloudSettings)) panel.Children.Add(CloudPanel());
        panel.Children.Add(editor);
        panel.Children.Add(Ui.Btn(Loc.T("common.save", "Save"), async () =>
        {
            object updated;
            try { updated = editor.Build(); editor.ShowError(null); }
            catch (FormatException e) { editor.ShowError(e.Message); return; }
            var current = await LoadAsync(key, type);
            updated = Merge(current, updated, type);
            var reason = Dialogs.Prompt($"Save {title}", "Describe the change for the audit trail.", minLength: 3);
            if (reason is null) return;
            await SaveAsync(key, type, updated, reason);
            if (type == typeof(LocalizationSettings)) await App.ApplyAppearanceAsync();
            if (type == typeof(AutomationSettings) && App.Host.Automation is { } a) await a.ReloadAsync();
            Dialogs.Info($"{title} saved.");
        }, "Button.Primary"));
        _editor.Content = panel;
    }

    private static async Task<object> LoadAsync(string key, Type type) => type.Name switch
    {
        nameof(CompanySettings) => await App.Host.Settings.GetAsync<CompanySettings>(key),
        nameof(LocalizationSettings) => await App.Host.Settings.GetAsync<LocalizationSettings>(key),
        nameof(WeighingSettings) => await App.Host.Settings.GetAsync<WeighingSettings>(key),
        nameof(ApprovalSettings) => await App.Host.Settings.GetAsync<ApprovalSettings>(key),
        nameof(PrintingSettings) => await App.Host.Settings.GetAsync<PrintingSettings>(key),
        nameof(AutomationSettings) => await App.Host.Settings.GetAsync<AutomationSettings>(key),
        nameof(SecuritySettings) => await App.Host.Settings.GetAsync<SecuritySettings>(key),
        nameof(BackupSettings) => await App.Host.Settings.GetAsync<BackupSettings>(key),
        _ => await App.Host.Settings.GetAsync<CloudSettings>(key),
    };

    private static Task SaveAsync(string key, Type type, object value, string reason) => value switch
    {
        CompanySettings v => App.Host.Settings.SaveAsync(key, v, Ui.User, reason),
        LocalizationSettings v => App.Host.Settings.SaveAsync(key, v, Ui.User, reason),
        WeighingSettings v => App.Host.Settings.SaveAsync(key, v, Ui.User, reason),
        ApprovalSettings v => App.Host.Settings.SaveAsync(key, v, Ui.User, reason),
        PrintingSettings v => App.Host.Settings.SaveAsync(key, v, Ui.User, reason),
        AutomationSettings v => App.Host.Settings.SaveAsync(key, v, Ui.User, reason),
        SecuritySettings v => App.Host.Settings.SaveAsync(key, v, Ui.User, reason),
        BackupSettings v => App.Host.Settings.SaveAsync(key, v, Ui.User, reason),
        CloudSettings v => App.Host.Settings.SaveAsync(key, v, Ui.User, reason),
        _ => throw new ArgumentException($"Unknown settings type {type.Name}."),
    };

    /// <summary>Values edited outside the generic editor (hidden fields, secrets) are taken from the stored copy.</summary>
    private object Merge(object current, object updated, Type type) => updated switch
    {
        WeighingSettings w => w with { ActiveWeighbridgeId = ((WeighingSettings)current).ActiveWeighbridgeId },
        CompanySettings c => c with { LogoBase64 = _pendingLogo ?? ((CompanySettings)current).LogoBase64 },
        LocalizationSettings l => l with { Language = _pendingLanguage ?? ((LocalizationSettings)current).Language, Theme = _pendingTheme ?? ((LocalizationSettings)current).Theme },
        CloudSettings c => c with { SiteKeyProtected = ((CloudSettings)current).SiteKeyProtected },
        _ => updated,
    };

    private string? _pendingLogo;
    private string? _pendingLanguage;
    private string? _pendingTheme;

    private UIElement LanguageTheme(LocalizationSettings current)
    {
        _pendingLanguage = current.Language;
        _pendingTheme = current.Theme;
        var lang = new ComboBox { ItemsSource = Loc.Languages.Select(l => l.Name).ToList(), SelectedIndex = Math.Max(0, Array.FindIndex(Loc.Languages, l => l.Code == current.Language)), Width = 220 };
        lang.SelectionChanged += (_, _) => _pendingLanguage = Loc.Languages[lang.SelectedIndex].Code;
        var theme = new ComboBox { ItemsSource = new[] { "Light", "Dark", "System" }, SelectedItem = current.Theme, Width = 160 };
        theme.SelectionChanged += (_, _) => _pendingTheme = (string)theme.SelectedItem;
        return Ui.Row(new StackPanel { Margin = new Thickness(0, 0, 12, 0), Children = { new TextBlock { Text = Loc.T("set.language", "Language"), Style = Ui.S("Text.Label") }, lang } },
            new StackPanel { Children = { new TextBlock { Text = Loc.T("set.theme", "Theme"), Style = Ui.S("Text.Label") }, theme } });
    }

    private UIElement LogoPanel()
    {
        _pendingLogo = null;
        return Ui.Row(Ui.Btn("Choose logo (PNG/JPEG, small)…", () =>
        {
            var path = Dialogs.OpenFile("Images (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg");
            if (path is null) return Task.CompletedTask;
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > 300_000) throw new ArgumentException("The logo file is larger than 300 KB. Use a smaller image.");
            _pendingLogo = Convert.ToBase64String(bytes);
            Dialogs.Info("Logo selected. Save to apply it.");
            return Task.CompletedTask;
        }), Ui.Btn("Remove logo", () => { _pendingLogo = string.Empty; return Task.CompletedTask; }));
    }

    private static UIElement PrinterPanel()
    {
        var printers = WpfPrintService.Printers();
        return Ui.Muted(printers.Count == 0 ? "No printers are installed on this PC." : "Installed printers (copy the exact name into 'Printer name'):  " + string.Join("  ·  ", printers));
    }

    private static UIElement CloudPanel() => new StackPanel
    {
        Children =
        {
            Ui.Muted("Optional. Weighing never depends on the cloud: data is queued locally and sent when the connection is available. Only HTTPS addresses are accepted. Passwords are never sent."),
            Ui.Row(
                Ui.Btn("Enter site key…", async () =>
                {
                    var key = Dialogs.Prompt("Site key", "Paste the key issued for this site by the cloud administrator. It is stored encrypted on this PC.", minLength: 10, label: "Site key");
                    if (key is null) return;
                    var cloud = await App.Host.Settings.GetAsync<CloudSettings>(SettingKeys.Cloud);
                    await App.Host.Settings.SaveAsync(SettingKeys.Cloud, cloud with { SiteKeyProtected = App.Host.Settings.Protector.Protect(key.Trim()) }, Ui.User, "Site key updated");
                    Dialogs.Info("Site key saved.");
                }),
                Ui.Btn("Test synchronisation now", async () =>
                {
                    if (App.Host.Cloud is not { } sync) return;
                    var s = await sync.RunOnceAsync();
                    if (s.Reachable) Dialogs.Info($"Connected. {s.Pending + s.Failed} item(s) waiting, {s.Conflicts} conflict(s).");
                    else Dialogs.Warning($"Not connected: {s.LastError}");
                })),
        },
    };

    private static async Task<UIElement> WeighbridgePanelAsync()
    {
        var limits = await App.Host.Weighing.LimitsAsync();
        var (wb, site) = await App.Host.Weighing.ActiveWeighbridgeAsync();
        var d = new StackPanel();
        d.Children.Add(Ui.H2($"Weighbridge {wb.Code} at {site.Name}"));
        var name = new TextBox { Text = wb.Name, Width = 300 };
        var cap = new TextBox { Text = limits.CapacityKg.ToString("0", System.Globalization.CultureInfo.CurrentCulture), Width = 140 };
        var min = new TextBox { Text = limits.MinimumKg.ToString("0", System.Globalization.CultureInfo.CurrentCulture), Width = 140 };
        var div = new TextBox { Text = (wb.DivisionG / 1000m).ToString("0.###", System.Globalization.CultureInfo.CurrentCulture), Width = 140 };
        foreach (var (label, box) in new[] { ("Name", name), ("Capacity (kg)", cap), ("Minimum weight to capture (kg)", min), ("Indicator division (kg)", div) })
        {
            d.Children.Add(new TextBlock { Text = label, Style = Ui.S("Text.Label") });
            d.Children.Add(box);
        }
        d.Children.Add(new Border { Height = 10 });
        d.Children.Add(Ui.Btn(Loc.T("common.save", "Save"), async () =>
        {
            static decimal P(TextBox b) => decimal.Parse(b.Text, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.CurrentCulture);
            await App.Host.Profiles.UpdateWeighbridgeAsync(Ui.User, name.Text, P(cap), P(min), P(div));
            Dialogs.Info("Weighbridge saved.");
        }, "Button.Primary"));
        return d;
    }
}

// ====================================================================== backup and restore

public sealed class BackupView : UserControl, IPage
{
    private readonly DataGrid _history = Ui.Grid(("Started", "StartedUtc"), ("Kind", "Kind"), ("Status", "Status"), ("File", "FilePath"), ("Size (bytes)", "SizeBytes"), ("Error", "Error"));
    private readonly TextBlock _info = new() { TextWrapping = TextWrapping.Wrap };

    public BackupView()
    {
        Content = Ui.Page(
            Ui.H1(Loc.T("nav.backup", "Backup & restore")),
            Ui.Card(_info, Ui.Row(
                Ui.Btn("Back up now", async () =>
                {
                    var info = await App.Host.Backups.BackupNowAsync(Ui.User);
                    Dialogs.Info($"Backup created:\n{info.Path}\n{info.SizeBytes / 1024.0 / 1024.0:0.0} MB, {info.Manifest.Transactions} transactions, SHA-256 {info.Sha256[..16]}…");
                    await LoadAsync();
                }, "Button.Primary"),
                Ui.Btn("Verify a backup file…", async () =>
                {
                    var path = Dialogs.OpenFile($"AWMS backup (*{SqliteBackup.Extension})|*{SqliteBackup.Extension}", App.Host.Paths.BackupDirectory);
                    if (path is null) return;
                    var info = await App.Host.Backups.VerifyAsync(path);
                    Dialogs.Info($"The backup is intact.\nCreated {info.Manifest.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm} by version {info.Manifest.ApplicationVersion}, {info.Manifest.Transactions} transactions.");
                }),
                Ui.Btn("Restore…", RestoreAsync, "Button.Danger"),
                Ui.Btn("Open backup folder", () => { Ui.OpenFolder(App.Host.Paths.BackupDirectory); return Task.CompletedTask; }))),
            Ui.Card(Ui.H2("History"), _history));
    }

    public async void OnShown() => await Dialogs.RunAsync(LoadAsync);

    public void OnHidden() { }

    private async Task LoadAsync()
    {
        var cfg = await App.Host.Settings.GetAsync<BackupSettings>(SettingKeys.Backup);
        _info.Text = $"Automatic backup: {(cfg.ScheduleEnabled ? $"daily at {cfg.DailyAt}" : "off")} · folder {cfg.Directory ?? App.Host.Paths.BackupDirectory}" +
                     $"{(cfg.SecondaryDirectory is { } s ? $" · second copy {s}" : " · no second copy configured (recommended: USB drive or network share)")} · keeps the last {cfg.KeepCount} backup files. " +
                     "Only old backup files are removed; transaction and audit records are never deleted automatically.";
        _history.ItemsSource = await App.Host.Backups.HistoryAsync();
    }

    private async Task RestoreAsync()
    {
        var path = Dialogs.OpenFile($"AWMS backup (*{SqliteBackup.Extension})|*{SqliteBackup.Extension}", App.Host.Paths.BackupDirectory);
        if (path is null) return;
        var info = await App.Host.Backups.VerifyAsync(path);
        var confirm = Dialogs.Prompt("Restore backup",
            $"The current database is replaced by the backup from {info.Manifest.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm} ({info.Manifest.Transactions} transactions). " +
            "Weighings recorded after that time are not in the backup. The current database is kept in a 'pre-restore' folder. Type RESTORE to continue.", label: "Confirmation");
        if (!string.Equals(confirm, "RESTORE", StringComparison.Ordinal)) return;
        var kept = await App.Host.Backups.RestoreAsync(Ui.User, path);
        Dialogs.Info($"Restored. The previous database was kept in:\n{kept}\n\nThe application will now close. Start it again.");
        System.Windows.Application.Current.Shutdown();
    }
}

// ====================================================================== audit trail

public sealed class AuditView : UserControl, IPage
{
    private readonly DataGrid _grid = Ui.Grid(("Time", "AtUtc"), ("User", "Username"), ("Category", "Category"), ("Action", "Action"), ("Summary", "Summary"), ("Reason", "Reason"), ("Machine", "Machine"));
    private readonly TextBox _text = new() { Width = 220 };
    private readonly ComboBox _category = new() { Width = 160, IsEditable = true, ItemsSource = new[] { "", "Weighing", "Ticket", "Security", "Settings", "Devices", "MasterData", "Automation", "Backup", "Cloud", "Reports", "System" } };
    private readonly DatePicker _from = new() { SelectedDate = DateTime.Today.AddDays(-7), Width = 130 };
    private readonly DatePicker _to = new() { SelectedDate = DateTime.Today, Width = 130 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };

    public AuditView()
    {
        Content = Ui.Page(
            Ui.H1(Loc.T("nav.audit", "Audit trail")),
            Ui.Muted("Every action is recorded in an append-only log. Each entry contains the hash of the previous one, so removing or altering an entry is detected by 'Verify chain'."),
            Ui.Card(
                Ui.Row(Labelled(Loc.T("common.from", "From"), _from), Labelled(Loc.T("common.to", "To"), _to), Labelled("Category", _category), Labelled(Loc.T("common.search", "Search"), _text)),
                Ui.Row(Ui.Btn(Loc.T("common.search", "Search"), LoadAsync, "Button.Primary"), Ui.Btn("Verify chain", VerifyAsync), Ui.Btn("Export (Excel)", ExportAsync)),
                _status),
            _grid);
        _grid.MaxHeight = 600;
    }

    private static UIElement Labelled(string label, FrameworkElement control) =>
        new StackPanel { Margin = new Thickness(0, 0, 12, 0), Children = { new TextBlock { Text = label, Style = Ui.S("Text.Label") }, control } };

    public async void OnShown() => await Dialogs.RunAsync(LoadAsync);

    public void OnHidden() { }

    private AuditFilter Filter() => new()
    {
        From = _from.SelectedDate is { } f ? new DateTimeOffset(f) : null,
        To = _to.SelectedDate is { } t ? new DateTimeOffset(t.AddDays(1)) : null,
        Category = string.IsNullOrWhiteSpace(_category.Text) ? null : _category.Text.Trim(),
        Text = string.IsNullOrWhiteSpace(_text.Text) ? null : _text.Text.Trim(),
        PageSize = 2000,
    };

    private async Task LoadAsync()
    {
        var result = await App.Host.Audit.QueryAsync(Ui.User, Filter());
        _grid.ItemsSource = result.Items;
        _status.Text = $"{result.Total} entries{(result.Total > result.Items.Count ? $" (showing the newest {result.Items.Count})" : string.Empty)}";
    }

    private async Task VerifyAsync()
    {
        var v = await App.Host.Audit.VerifyAsync();
        _status.Text = (v.Valid ? "✔ " : "✖ ") + v.Message;
        _status.Foreground = (System.Windows.Media.Brush)FindResource(v.Valid ? "Brush.Ok" : "Brush.Danger");
    }

    private async Task ExportAsync()
    {
        var f = Filter();
        var report = await App.Host.Reports.ExportAsync(Ui.User, new ReportRequest(ReportKind.AuditTrail, new TransactionFilter { From = f.From, To = f.To, Text = f.Text }), ExportFormat.Xlsx);
        var path = Dialogs.SaveFile("Excel (*.xlsx)|*.xlsx", report.FileName, App.Host.Paths.ExportDirectory);
        if (path is null) return;
        await File.WriteAllBytesAsync(path, report.Content);
        Dialogs.Info($"Exported {report.Rows} entries to {path}");
    }
}

// ====================================================================== notifications

public sealed class NotificationsView : UserControl, IPage
{
    private readonly DataGrid _grid = Ui.Grid(("Time", "AtUtc"), ("Severity", "Severity"), ("Category", "Category"), ("Title", "Title"), ("Message", "Message"));

    public NotificationsView()
    {
        Content = Ui.Page(
            Ui.H1(Loc.T("nav.notifications", "Notifications")),
            Ui.Row(Ui.Btn("Mark all as read", async () => { await App.Host.Notifications.MarkAllReadAsync(); await LoadAsync(); }), Ui.Btn(Loc.T("common.refresh", "Refresh"), LoadAsync)),
            _grid);
    }

    public async void OnShown() => await Dialogs.RunAsync(LoadAsync);

    public void OnHidden() { }

    private async Task LoadAsync() => _grid.ItemsSource = await App.Host.Notifications.RecentAsync(300);
}

// ====================================================================== system status

public sealed class StatusView : UserControl, IPage
{
    private readonly TextBox _text = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = null, VerticalContentAlignment = VerticalAlignment.Top };

    public StatusView()
    {
        _text.FontFamily = (System.Windows.Media.FontFamily)FindResource("Font.Mono");
        Content = Ui.Page(
            Ui.H1(Loc.T("nav.about", "System status")),
            Ui.Row(Ui.Btn(Loc.T("common.refresh", "Refresh"), LoadAsync), Ui.Btn("Open data folder", () => { Ui.OpenFolder(App.Host.Paths.Root); return Task.CompletedTask; }),
                Ui.Btn("Open log folder", () => { Ui.OpenFolder(App.Host.Paths.LogDirectory); return Task.CompletedTask; })),
            Ui.Card(_text));
    }

    public async void OnShown() => await Dialogs.RunAsync(LoadAsync);

    public void OnHidden() { }

    private async Task LoadAsync()
    {
        var host = App.Host;
        var snap = host.Indicator.Snapshot;
        var health = await host.Health.CheckAsync(snap.Connection);
        CloudStatus? cloud = host.Cloud?.Status;
        var lines = new List<string>
        {
            $"Advanced Weighbridge {Startup.Commands.Version} on {Environment.MachineName} ({Environment.OSVersion}, .NET {Environment.Version})",
            $"Data folder: {host.Paths.Root}",
            $"Database: {health.DatabaseProvider}, schema version {health.SchemaVersion}, {(health.DatabaseOk ? "OK" : "PROBLEM")}",
            $"Disk free: {health.DiskFreeGb:0.0} GB",
            $"Last backup: {health.LastBackupUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "never"}",
            $"Indicator: {snap.Connection} · {snap.PortDescription} · profile {snap.ProfileName} v{snap.ProfileVersion} · {snap.FramesParsed} accepted / {snap.FramesRejected} rejected · reconnects {snap.ReconnectCount}",
            $"Automation: {host.Automation?.Status.Mode} · {host.Automation?.Status.State}",
            $"I/O hardware: {(host.Hardware is null ? "none" : $"{host.Hardware.Config.Controllers.Count} controller(s)")} · cameras {host.Cameras.Cameras.Count} · identification {string.Join(", ", host.Identification.SourceNames)}",
            $"Cloud: {(cloud is null || !cloud.Enabled ? "off" : cloud.Reachable ? "connected" : $"offline – {cloud.LastError}")} · waiting {health.Sync.Pending}, retrying {health.Sync.Failed}, conflicts {health.Sync.Conflicts}",
            $"Plug-ins: {(host.Plugins.Count == 0 ? "none" : string.Join(", ", host.Plugins.Select(p => p.ToString())))}",
            $"Previous shutdown: {(health.PreviousShutdownUnclean ? "NOT clean (power loss or crash) – data integrity was checked at start-up" : "clean")}",
            string.Empty,
            health.Warnings.Count == 0 ? "No warnings." : "Warnings:\n  • " + string.Join("\n  • ", health.Warnings),
        };
        var audit = await host.Audit.VerifyAsync();
        lines.Add($"Audit chain: {(audit.Valid ? "intact" : "BROKEN")} – {audit.Message}");
        _text.Text = string.Join(Environment.NewLine, lines);
    }
}
