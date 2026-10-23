using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Weighbridge.Core.Indicators;
using Weighbridge.Core.Security;
using Weighbridge.Desktop.Infrastructure;
using Weighbridge.Desktop.Views;

namespace Weighbridge.Desktop.Windows;

/// <summary>A page shown in the main window. Pages are created once and kept while the user is signed in.</summary>
public interface IPage
{
    void OnShown();
    void OnHidden();
}

public sealed record NavEntry(string Key, string TextKey, string? Permission, Func<FrameworkElement> Create, bool Group = false);

public partial class MainWindow : Window
{
    private readonly Dictionary<string, FrameworkElement> _pages = [];
    private readonly DispatcherTimer _status;
    private readonly DispatcherTimer _session;
    private string? _current;

    public static readonly NavEntry[] Entries =
    [
        new("weighing", "nav.weighing", Permissions.DashboardView, () => new WeighingView()),
        new("history", "nav.history", Permissions.HistoryView, () => new HistoryView()),
        new("reports", "nav.reports", Permissions.ReportsView, () => new ReportsView()),
        new("master", "nav.master", Permissions.MasterDataView, () => new MasterDataView()),
        new("devices", "nav.devices", null, () => new Border(), Group: true),
        new("wizard", "nav.wizard", Permissions.DevicesConfigure, () => new SetupWizardView()),
        new("monitor", "nav.monitor", Permissions.DevicesView, () => new RawMonitorView()),
        new("hardware", "nav.hardware", Permissions.DevicesView, () => new HardwareView()),
        new("admin", "nav.admin", null, () => new Border(), Group: true),
        new("users", "nav.users", Permissions.UsersManage, () => new UsersView()),
        new("settings", "nav.settings", Permissions.SettingsManage, () => new SettingsView()),
        new("backup", "nav.backup", Permissions.BackupManage, () => new BackupView()),
        new("audit", "nav.audit", Permissions.AuditView, () => new AuditView()),
        new("notifications", "nav.notifications", Permissions.DashboardView, () => new NotificationsView()),
        new("status", "nav.about", Permissions.DashboardView, () => new StatusView()),
    ];

    public MainWindow()
    {
        InitializeComponent();
        App.UserChanged += (_, user) => Dispatcher.Invoke(() => OnUserChanged(user));
        Loc.Changed += (_, _) => BuildNavigation();
        _status = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => UpdateStatus(), Dispatcher);
        _session = new DispatcherTimer(TimeSpan.FromSeconds(20), DispatcherPriority.Background, (_, _) => CheckSession(), Dispatcher);
        PreviewKeyDown += (_, _) => App.Host.Session.Touch();
        PreviewMouseDown += (_, _) => App.Host.Session.Touch();
        Loaded += (_, _) =>
        {
            _status.Start();
            _session.Start();
            OnUserChanged(App.Host.Session.User);
        };
        Closing += (_, e) =>
        {
            if (App.Options.Screenshots is null && !Dialogs.Confirm("Close Advanced Weighbridge? Automatic weighing, lights and barriers controlled by this PC stop until it is started again."))
            {
                e.Cancel = true;
            }
        };
    }

    /// <summary>Shows a page by key (used by the navigation, links between pages and the screenshot runner).</summary>
    public void Navigate(string key)
    {
        var user = App.Host.Session.User;
        var entry = Entries.FirstOrDefault(e => e.Key == key && !e.Group);
        if (entry is null || user is null || entry.Permission is { } p && !user.Has(p)) return;
        if (_current is not null && _pages.TryGetValue(_current, out var old) && old is IPage oldPage) oldPage.OnHidden();
        if (!_pages.TryGetValue(key, out var page))
        {
            page = entry.Create();
            _pages[key] = page;
        }
        _current = key;
        PageHost.Content = page;
        if (page is IPage shown) shown.OnShown();
        foreach (var rb in NavPanel.Children.OfType<RadioButton>()) rb.IsChecked = (string)rb.Tag == key;
    }

    public FrameworkElement? Page(string key) => _pages.GetValueOrDefault(key);

    private void OnUserChanged(UserContext? user)
    {
        if (user is null)
        {
            foreach (var p in _pages.Values.OfType<IDisposable>()) p.Dispose();
            _pages.Clear();
            _current = null;
            PageHost.Content = null;
            NavPanel.Children.Clear();
            return;
        }
        UserText.Text = $"{user.DisplayName} ({user.Role})";
        BuildNavigation();
        Navigate(_current ?? "weighing");
    }

    private void BuildNavigation()
    {
        var user = App.Host.Session.User;
        NavPanel.Children.Clear();
        if (user is null) return;
        foreach (var e in Entries)
        {
            if (e.Group)
            {
                NavPanel.Children.Add(new TextBlock
                {
                    Text = Loc.T(e.TextKey).ToUpperInvariant(), Foreground = (Brush)FindResource("Brush.NavText"), Opacity = 0.6, FontSize = 11,
                    Margin = new Thickness(18, 14, 0, 4), FontWeight = FontWeights.SemiBold,
                });
                continue;
            }
            if (e.Permission is { } p && !user.Has(p)) continue;
            var item = new RadioButton { Content = Loc.T(e.TextKey), Tag = e.Key, Style = (Style)FindResource("Nav.Item"), GroupName = "nav", IsChecked = e.Key == _current };
            item.Checked += (_, _) => { if (_current != e.Key) Navigate(e.Key); };
            NavPanel.Children.Add(item);
        }
    }

    private void UpdateStatus()
    {
        var host = App.Host;
        ClockText.Text = DateTime.Now.ToString("yyyy-MM-dd  HH:mm:ss");
        var snap = host.Indicator.Snapshot;
        var simulator = host.Indicator.Profile?.Connection.Transport == TransportKind.Simulator;
        var (text, brush) = snap.Connection switch
        {
            ConnectionState.Connected => ($"Indicator: connected{(simulator ? " (SIMULATOR)" : string.Empty)}", simulator ? "Brush.StateBlue" : "Brush.StateGreen"),
            ConnectionState.Silent => ("Indicator: no data", "Brush.StateRed"),
            ConnectionState.Error => ("Indicator: error", "Brush.StateRed"),
            ConnectionState.Connecting => ("Indicator: connecting…", "Brush.StateYellow"),
            _ => ("Indicator: not connected", "Brush.StateGrey"),
        };
        IndicatorText.Text = text;
        IndicatorPill.Background = (Brush)FindResource(brush);
        IndicatorPill.ToolTip = snap.PortDescription is { } port ? $"{port} · {snap.ProfileName} v{snap.ProfileVersion}" : snap.LastError;

        var cloud = host.Cloud?.Status;
        if (cloud is null || !cloud.Enabled)
        {
            CloudText.Text = Loc.T("cloud.disabled", "Cloud: off");
            CloudPill.Background = (Brush)FindResource("Brush.StateGrey");
        }
        else
        {
            CloudText.Text = (cloud.Reachable ? Loc.T("cloud.online", "Cloud: connected") : Loc.T("cloud.offline", "Cloud: offline (data queued)"))
                             + (cloud.Pending + cloud.Failed > 0 ? $" · {cloud.Pending + cloud.Failed} waiting" : string.Empty);
            CloudPill.Background = (Brush)FindResource(cloud.Reachable ? "Brush.StateGreen" : "Brush.StateYellow");
            CloudPill.ToolTip = cloud.LastError;
        }
        ModeText.Text = host.Automation is { } a ? $"{Loc.T("w.mode", "Mode")}: {Loc.T("w.mode." + a.Mode, a.Mode.ToString())}" : string.Empty;
    }

    private void CheckSession()
    {
        if (App.Options.Screenshots is not null || !App.Host.Session.IsSignedIn) return;
        if (!App.Host.Session.CheckTimeout()) return;
        App.SignOut();
        Relogin("Your session ended after a period of inactivity. Sign in again.");
    }

    private void OnSignOut(object sender, RoutedEventArgs e)
    {
        App.SignOut();
        Relogin(null);
    }

    private void Relogin(string? message)
    {
        Hide();
        if (message is not null) Dialogs.Info(message);
        while (App.SignIn() is null)
        {
            if (Dialogs.Confirm("Close Advanced Weighbridge?"))
            {
                System.Windows.Application.Current.Shutdown();
                return;
            }
        }
        Show();
        Activate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.F9 && _current != "weighing" && App.Host.Session.User?.Has(Permissions.WeighingCapture) == true)
        {
            Navigate("weighing");
            e.Handled = true;
        }
    }
}
