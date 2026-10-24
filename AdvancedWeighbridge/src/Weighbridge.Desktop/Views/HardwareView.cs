using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Weighbridge.Application.Settings;
using Weighbridge.Core.Automation;
using Weighbridge.Core.Indicators;
using Weighbridge.Core.Security;
using Weighbridge.Devices.IO;
using Weighbridge.Desktop.Infrastructure;
using Weighbridge.Desktop.Windows;

namespace Weighbridge.Desktop.Views;

/// <summary>
/// Traffic lights, barriers and sensors: live state, manual control (with reason, permission and the same safety
/// interlocks as automatic mode), the hardware configuration, and simulator controls for training.
/// </summary>
public sealed class HardwareView : UserControl, IPage
{
    private readonly TextBlock _state = new() { TextWrapping = TextWrapping.Wrap, FontSize = 15 };
    private readonly TextBlock _sensors = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _manual = new();
    private readonly StackPanel _simulator = new();
    private readonly TextBox _config = new() { AcceptsReturn = true, Height = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    public HardwareView()
    {
        _config.FontFamily = (FontFamily)FindResource("Font.Mono");
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = Loc.T("nav.hardware", "Lights & barriers"), Style = S("Text.H1") });
        panel.Children.Add(Card("Live state", new StackPanel { Children = { _state, _sensors } }));
        panel.Children.Add(Card("Manual control", _manual));
        panel.Children.Add(Card("Simulator (training)", _simulator));
        panel.Children.Add(Card("Hardware configuration", ConfigPanel()));
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _timer.Tick += (_, _) => Refresh();
    }

    private static Style S(string key) => (Style)System.Windows.Application.Current.FindResource(key);
    private static UserContext User => App.Host.Session.Require();

    private static Border Card(string title, UIElement content) => new()
    {
        Style = S("Card"),
        Child = new StackPanel { Children = { new TextBlock { Text = title, Style = S("Text.H2"), Margin = new Thickness(0, 0, 0, 6) }, content } },
    };

    public async void OnShown()
    {
        BuildManual();
        BuildSimulator();
        var config = await App.Host.Settings.GetAsync<HardwareConfig>(SettingKeys.Hardware);
        _config.Text = JsonSerializer.Serialize(config, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
        _config.IsReadOnly = !User.Has(Permissions.SettingsManage);
        Refresh();
        _timer.Start();
    }

    public void OnHidden() => _timer.Stop();

    private void Refresh()
    {
        var auto = App.Host.Automation?.Status;
        var hw = App.Host.Hardware?.Status;
        _state.Text = auto is null ? "Automation is not running." :
            $"Mode {auto.Mode} · state {auto.State}{(auto.Fault != FaultKind.None ? $" · FAULT {auto.Fault}: {auto.FaultMessage}" : string.Empty)}\n" +
            $"Entry light {auto.EntryLight} · exit light {auto.ExitLight} · exit barrier {(auto.ExitOpen ? "OPEN" : "closed")}" +
            (auto.Vehicle is { } v ? $"\nVehicle {v.Registration} ({v.Source})" : string.Empty) + (auto.LastEvent is { } e ? $"\nLast event: {e}" : string.Empty);
        _sensors.Text = hw is null
            ? "No I/O controller is configured. Lights, barriers and sensors are not used; weighing works normally."
            : $"Sensors: presence {Fmt(hw.Sensors?.Presence)}, position OK {Fmt(hw.Sensors?.PositionOk)}, safety {(hw.Sensors?.SafetyOk == false ? "NOT OK" : "OK")}" +
              $" · entry barrier {Fmt(hw.EntryBarrierOpen, "open", "closed")}, exit barrier {Fmt(hw.ExitBarrierOpen, "open", "closed")} · alarm {(hw.AlarmOn ? "ON" : "off")}" +
              (hw.LastError is { } err ? $"\nLast I/O error: {err}" : string.Empty) + (hw.Sensors?.Error is { } se ? $"\nSensor error: {se}" : string.Empty);
    }

    private static string Fmt(bool? v, string yes = "yes", string no = "no") => v switch { true => yes, false => no, _ => "unknown" };

    private void BuildManual()
    {
        _manual.Children.Clear();
        if (!User.Has(Permissions.HardwareOverride))
        {
            _manual.Children.Add(new TextBlock { Text = "You do not have the permission to operate the barriers and lights manually.", Style = S("Text.Muted") });
            return;
        }
        _manual.Children.Add(new TextBlock
        {
            Text = "Every manual action needs a reason and is recorded. The safety interlocks still apply: a barrier is never opened while the deck reports an unsafe condition.",
            Style = S("Text.Muted"), Margin = new Thickness(0, 0, 0, 8),
        });
        var row = new WrapPanel();
        foreach (var (label, barrier, open) in new[] { ("Open entry barrier", BarrierId.Entry, true), ("Close entry barrier", BarrierId.Entry, false), ("Open exit barrier", BarrierId.Exit, true), ("Close exit barrier", BarrierId.Exit, false) })
        {
            var b = new Button { Content = label, Margin = new Thickness(0, 0, 8, 8) };
            b.Click += async (_, _) =>
            {
                var reason = Dialogs.Prompt(label, "Why is the barrier operated manually?", minLength: 5);
                if (reason is null || App.Host.Automation is not { } a) return;
                await Dialogs.RunAsync(() => a.ManualBarrierAsync(User, barrier, open, reason));
            };
            row.Children.Add(b);
        }
        foreach (var (label, entry, exit) in new[] { ("Lights: entry green, exit red", LightColor.Green, LightColor.Red), ("Lights: all red", LightColor.Red, LightColor.Red), ("Lights: exit green", LightColor.Red, LightColor.Green) })
        {
            var b = new Button { Content = label, Margin = new Thickness(0, 0, 8, 8) };
            b.Click += async (_, _) =>
            {
                var reason = Dialogs.Prompt(label, "Why are the lights set manually?", minLength: 5);
                if (reason is null || App.Host.Automation is not { } a) return;
                await Dialogs.RunAsync(() => a.ManualLightsAsync(User, entry, exit, reason));
            };
            row.Children.Add(b);
        }
        _manual.Children.Add(row);
        var reg = new TextBox { Width = 180 };
        var submit = new Button { Content = "Submit vehicle number (manual identification)", Margin = new Thickness(8, 0, 0, 0) };
        submit.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(reg.Text)) App.Host.Identification.Submit(reg.Text.Trim());
        };
        _manual.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { reg, submit } });
    }

    private void BuildSimulator()
    {
        _simulator.Children.Clear();
        var isSim = App.Host.Indicator.Profile?.Connection.Transport == TransportKind.Simulator;
        if (!isSim)
        {
            _simulator.Children.Add(new TextBlock { Text = "The simulator is not the active indicator.", Style = S("Text.Muted") });
            return;
        }
        var world = App.Host.Simulator;
        _simulator.Children.Add(new TextBlock { Text = "Drive simulated trucks on and off the weighbridge. Recorded weights are labelled SIMULATOR.", Style = S("Text.Muted"), Margin = new Thickness(0, 0, 0, 8) });
        var row = new WrapPanel();
        void Add(string label, Action action)
        {
            var b = new Button { Content = label, Margin = new Thickness(0, 0, 8, 8) };
            b.Click += (_, _) => action();
            row.Children.Add(b);
        }
        foreach (var truck in world.Fleet.Take(3))
        {
            Add($"{truck.Registration} on (loaded)", () => world.DriveOn(truck, loaded: true));
            Add($"{truck.Registration} on (empty)", () => world.DriveOn(truck, loaded: false));
        }
        Add("Drive off", world.DriveOff);
        _simulator.Children.Add(row);
        var auto = new CheckBox { Content = "Automatic traffic (trucks come and go)", IsChecked = world.AutoCycle };
        auto.Click += (_, _) => world.AutoCycle = auto.IsChecked == true;
        var overload = new CheckBox { Content = "Simulate overload", IsChecked = world.ForceOverload };
        overload.Click += (_, _) => world.ForceOverload = overload.IsChecked == true;
        var disconnect = new CheckBox { Content = "Simulate cable disconnected", IsChecked = world.SimulateDisconnect };
        disconnect.Click += (_, _) => world.SimulateDisconnect = disconnect.IsChecked == true;
        _simulator.Children.Add(new WrapPanel { Children = { auto, overload, disconnect } });
    }

    private UIElement ConfigPanel()
    {
        var p = new StackPanel();
        p.Children.Add(new TextBlock
        {
            Text = "I/O controllers (Modbus TCP/RTU relay modules, simulated), signal assignments, cameras and vehicle identification sources, as JSON. " +
                   "Only the components listed here are used. Restart the application after saving.",
            Style = S("Text.Muted"), Margin = new Thickness(0, 0, 0, 6),
        });
        p.Children.Add(_config);
        var save = new Button { Content = Loc.T("common.save", "Save"), Style = S("Button.Primary") };
        save.Click += async (_, _) => await Dialogs.RunAsync(async () =>
        {
            HardwareConfig config;
            try
            {
                config = JsonSerializer.Deserialize<HardwareConfig>(_config.Text, new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })
                         ?? throw new FormatException("Empty configuration.");
            }
            catch (JsonException e)
            {
                throw new FormatException("The configuration is not valid JSON: " + e.Message);
            }
            var reason = Dialogs.Prompt("Save hardware configuration", "Describe the change for the audit trail.", minLength: 3);
            if (reason is null) return;
            await App.Host.Settings.SaveAsync(SettingKeys.Hardware, config, User, reason);
            Dialogs.Info("Saved. Restart the application to apply the new hardware configuration.");
        });
        var simulated = new Button { Content = "Insert simulated lights, barriers, sensors and ANPR" };
        simulated.Click += (_, _) => _config.Text = JsonSerializer.Serialize(HardwareConfig.CreateSimulated(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
        var none = new Button { Content = "No I/O hardware" };
        none.Click += (_, _) => _config.Text = "{}";
        p.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0), Children = { save, simulated, none } });
        return p;
    }
}
