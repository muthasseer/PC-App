using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Weighbridge.Core.Common;
using Weighbridge.Core.Indicators;
using Weighbridge.Core.Indicators.Framing;
using Weighbridge.Core.Indicators.Learning;
using Weighbridge.Core.Indicators.Parsing;
using Weighbridge.Core.Indicators.Stability;
using Weighbridge.Core.Security;
using Weighbridge.Core.Units;
using Weighbridge.Devices.Discovery;
using Weighbridge.Devices.Indicator;
using Weighbridge.Devices.Transports;
using Weighbridge.Desktop.Infrastructure;
using Weighbridge.Desktop.Windows;

namespace Weighbridge.Desktop.Views;

/// <summary>
/// Indicator set-up wizard: choose the connection (with automatic line-setting detection), capture raw output, let the
/// software suggest the framing and the data format, test live, confirm against the value shown on the indicator, then
/// save the profile. Nothing is activated until the operator saves; the running indicator is paused while testing.
/// </summary>
public sealed class SetupWizardView : UserControl, IPage, IDisposable
{
    private static readonly string[] StepTitles = ["1. Connection", "2. Raw data and framing", "3. Data format", "4. Live test and reference weight", "5. Stability and save"];

    private readonly ContentControl _content = new();
    private readonly StackPanel _steps = new() { Margin = new Thickness(0, 0, 16, 0) };
    private readonly Button _back = new() { Content = "← Back" };
    private readonly Button _next = new() { Content = "Next →" };
    private readonly TextBlock _note = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
    private readonly List<byte> _raw = [];
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private IndicatorProfile _profile = new() { Name = "New indicator" };
    private IndicatorService? _service;
    private FormatAnalysis? _analysis;
    private int _step;
    private bool _paused;
    private Action? _onTick;

    public SetupWizardView()
    {
        _back.Click += (_, _) => Go(_step - 1);
        _next.Click += (_, _) => Go(_step + 1);
        _next.Style = (Style)System.Windows.Application.Current.FindResource("Button.Primary");
        _refresh.Tick += (_, _) => _onTick?.Invoke();
        var nav = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        nav.Children.Add(_back);
        nav.Children.Add(_next);
        DockPanel.SetDock(nav, Dock.Bottom);
        var title = new TextBlock { Text = Loc.T("nav.wizard", "Indicator setup"), Style = St("Text.H1") };
        DockPanel.SetDock(title, Dock.Top);
        DockPanel.SetDock(_note, Dock.Top);
        DockPanel.SetDock(_steps, Dock.Left);
        var card = new Border { Style = St("Card"), Child = new ScrollViewer { Content = _content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        Content = new DockPanel { Margin = new Thickness(16), Children = { title, _note, nav, _steps, card } };
    }

    private static Style St(string key) => (Style)System.Windows.Application.Current.FindResource(key);
    private static Core.Security.UserContext User => App.Host.Session.Require();

    public async void OnShown()
    {
        if (_service is not null) return;
        var (active, port) = await App.Host.Profiles.ActiveAsync();
        _profile = active is null ? new IndicatorProfile { Name = "New indicator" }
            : port is null ? active : active with { Connection = active.Connection with { Serial = active.Connection.Serial with { PortName = port } } };
        _note.Text = $"Current profile: {(active is null ? "none" : $"{active.Name} v{active.Version}{(active.Verified ? " (verified)" : " (NOT verified)")}")}. " +
                     "Changes take effect only when you save in step 5. While you test, the running indicator connection is paused and weighing is not possible.";
        Go(0);
    }

    public void OnHidden() => StopService(resume: true);

    // ------------------------------------------------------------------ navigation

    private void Go(int step)
    {
        if (step < 0 || step >= StepTitles.Length) return;
        if (step > _step && !Validate()) return;
        if (_step == 3 && step != 3) StopService(resume: false);
        _step = step;
        _onTick = null;
        _steps.Children.Clear();
        for (var i = 0; i < StepTitles.Length; i++)
        {
            _steps.Children.Add(new TextBlock
            {
                Text = (i < step ? "✔ " : i == step ? "▶ " : "   ") + StepTitles[i], Margin = new Thickness(0, 6, 0, 6),
                FontWeight = i == step ? FontWeights.SemiBold : FontWeights.Normal, Foreground = (Brush)FindResource(i == step ? "Brush.Accent" : "Brush.Text"),
            });
        }
        _back.IsEnabled = step > 0;
        _next.Visibility = step < StepTitles.Length - 1 ? Visibility.Visible : Visibility.Collapsed;
        _content.Content = step switch
        {
            0 => ConnectionStep(),
            1 => RawStep(),
            2 => FormatStep(),
            3 => LiveStep(),
            _ => SaveStep(),
        };
    }

    private bool Validate()
    {
        var problems = _profile.Validate(App.Host.Protocols).Where(p => _step >= 2 || !p.StartsWith("Parser", StringComparison.Ordinal)).ToList();
        if (_step == 0 && problems.Count > 0)
        {
            Dialogs.Warning(string.Join("\n", problems));
            return false;
        }
        if (_step == 1 && _raw.Count == 0 && _profile.Connection.Transport != TransportKind.Simulator && !Dialogs.Confirm("No data has been captured yet. Continue anyway?")) return false;
        return true;
    }

    // ------------------------------------------------------------------ step 1: connection

    private UIElement ConnectionStep()
    {
        var p = new StackPanel();
        p.Children.Add(Para("Choose how the indicator is connected. For USB-to-serial adapters, install the driver from the adapter manufacturer (shown below when Windows reports a problem); the application never downloads drivers itself."));
        var kind = Combo(Enum.GetValues<TransportKind>(), _profile.Connection.Transport);
        p.Children.Add(Field("Connection type", kind));
        var detail = new ContentControl();
        p.Children.Add(detail);
        void Show()
        {
            var transport = (TransportKind)kind.SelectedItem!;
            _profile = _profile with { Connection = _profile.Connection with { Transport = transport } };
            detail.Content = transport switch
            {
                TransportKind.Serial => SerialPanel(),
                TransportKind.TcpClient => TcpPanel(),
                TransportKind.Replay => ReplayPanel(),
                _ => SimulatorPanel(),
            };
        }
        kind.SelectionChanged += (_, _) => Show();
        Show();
        return p;
    }

    private UIElement SerialPanel()
    {
        var s = _profile.Connection.Serial;
        var p = new StackPanel();
        var ports = new ComboBox { IsEditable = true, Width = 360, HorizontalAlignment = HorizontalAlignment.Left, Text = s.PortName };
        var portInfo = new TextBlock { TextWrapping = TextWrapping.Wrap, Style = St("Text.Muted") };
        void LoadPorts()
        {
            IReadOnlyList<SerialPortInfo> list;
            try { list = PortDiscovery.List(); }
            catch (Exception e) when (e is InvalidOperationException or UnauthorizedAccessException or IOException) { list = []; portInfo.Text = e.Message; }
            ports.ItemsSource = list;
            ports.DisplayMemberPath = nameof(SerialPortInfo.Display);
            ports.SelectedItem = list.FirstOrDefault(x => x.PortName == s.PortName);
            if (list.Count == 0) portInfo.Text = "No COM ports found. Check the cable and the adapter driver (Device Manager → Ports).";
        }
        ports.SelectionChanged += (_, _) =>
        {
            if (ports.SelectedItem is not SerialPortInfo info) return;
            Update(x => x with { PortName = info.PortName });
            portInfo.Text = info.DriverProblem
                ? $"Windows reports a driver problem (code {info.ConfigManagerErrorCode}). Install the driver from the manufacturer{(info.DriverHelpUrl is { } url ? $": {url}" : ".")}"
                : $"{info.Manufacturer} {info.ChipVendor}".Trim();
        };
        ports.LostFocus += (_, _) => { if (ports.SelectedItem is null && !string.IsNullOrWhiteSpace(ports.Text)) Update(x => x with { PortName = ports.Text.Trim().ToUpperInvariant() }); };
        var refresh = new Button { Content = "Refresh ports" };
        refresh.Click += (_, _) => LoadPorts();
        var portRow = new StackPanel { Orientation = Orientation.Horizontal };
        portRow.Children.Add(ports);
        portRow.Children.Add(new Border { Width = 8 });
        portRow.Children.Add(refresh);
        p.Children.Add(Field("COM port", portRow));
        p.Children.Add(portInfo);
        LoadPorts();

        var baud = Combo(new[] { 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200 }, s.BaudRate);
        baud.IsEditable = true;
        baud.LostFocus += (_, _) => { if (int.TryParse(baud.Text, out var b)) Update(x => x with { BaudRate = b }); };
        baud.SelectionChanged += (_, _) => { if (baud.SelectedItem is int b) Update(x => x with { BaudRate = b }); };
        var bits = Combo(new[] { 7, 8, 6, 5 }, s.DataBits);
        bits.SelectionChanged += (_, _) => Update(x => x with { DataBits = (int)bits.SelectedItem! });
        var parity = Combo(Enum.GetValues<ParityKind>(), s.Parity);
        parity.SelectionChanged += (_, _) => Update(x => x with { Parity = (ParityKind)parity.SelectedItem! });
        var stop = Combo(Enum.GetValues<StopBitsKind>(), s.StopBits);
        stop.SelectionChanged += (_, _) => Update(x => x with { StopBits = (StopBitsKind)stop.SelectedItem! });
        var hand = Combo(Enum.GetValues<HandshakeKind>(), s.Handshake);
        hand.SelectionChanged += (_, _) => Update(x => x with { Handshake = (HandshakeKind)hand.SelectedItem! });
        var row = new WrapPanel();
        row.Children.Add(Field("Baud rate", baud, 130));
        row.Children.Add(Field("Data bits", bits, 90));
        row.Children.Add(Field("Parity", parity, 110));
        row.Children.Add(Field("Stop bits", stop, 130));
        row.Children.Add(Field("Handshake", hand, 190));
        p.Children.Add(row);
        var dtr = new CheckBox { Content = "DTR on", IsChecked = s.DtrEnable };
        dtr.Click += (_, _) => Update(x => x with { DtrEnable = dtr.IsChecked == true });
        var rts = new CheckBox { Content = "RTS on", IsChecked = s.RtsEnable };
        rts.Click += (_, _) => Update(x => x with { RtsEnable = rts.IsChecked == true });
        p.Children.Add(new WrapPanel { Children = { dtr, rts } });
        p.Children.Add(PollPanel());

        var scanResults = new ListBox { Height = 170, DisplayMemberPath = nameof(LineScore.Settings) };
        var scanButton = new Button { Content = "Detect line settings automatically", Style = St("Button.Primary"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 6) };
        var scanStatus = new TextBlock { Style = St("Text.Muted") };
        scanButton.Click += async (_, _) =>
        {
            PauseLiveIndicator();
            scanButton.IsEnabled = false;
            var results = new List<LineScore>();
            scanResults.ItemsSource = null;
            try
            {
                var progress = new Progress<LineScore>(score =>
                {
                    results.Add(score);
                    scanStatus.Text = $"Tried {score.Settings}: {score.Bytes} bytes, printable {score.PrintableRatio:P0}, score {score.Score:0.00}";
                });
                var poll = _profile.Poll.Enabled ? ByteText.Unescape(_profile.Poll.Command) : null;
                var final = await new BaudScanner().ScanAsync(_profile.Connection.Serial.PortName, pollCommand: poll, progress: progress);
                var ranked = final.OrderByDescending(r => r.Score).ToList();
                scanResults.ItemsSource = ranked.Select(r => new ScanRow(r)).ToList();
                scanResults.DisplayMemberPath = nameof(ScanRow.Text);
                var best = ranked.FirstOrDefault();
                scanStatus.Text = best is { LooksValid: true }
                    ? $"Best match: {best.Settings} (score {best.Score:0.00}). Select a line to use it."
                    : "No setting produced readable data. Check the cable (TX/RX may need a null-modem), that the indicator's serial output is enabled, and whether it must be polled.";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                scanStatus.Text = $"The port could not be opened: {e.Message}";
            }
            finally
            {
                scanButton.IsEnabled = true;
            }
        };
        scanResults.SelectionChanged += (_, _) =>
        {
            if (scanResults.SelectedItem is not ScanRow r) return;
            var c = r.Score.Settings;
            Update(x => x with { BaudRate = c.BaudRate, DataBits = c.DataBits, Parity = c.Parity, StopBits = c.StopBits });
            baud.SelectedItem = c.BaudRate;
            bits.SelectedItem = c.DataBits;
            parity.SelectedItem = c.Parity;
            stop.SelectedItem = c.StopBits;
        };
        p.Children.Add(scanButton);
        p.Children.Add(scanStatus);
        p.Children.Add(scanResults);
        return p;
    }

    private sealed record ScanRow(LineScore Score)
    {
        public string Text => $"{Score.Settings}   score {Score.Score:0.00}   {Score.Bytes} bytes   printable {Score.PrintableRatio:P0}   digits {Score.DigitRatio:P0}{(Score.HasDelimiters ? "   line endings found" : string.Empty)}";
    }

    private UIElement PollPanel()
    {
        var poll = new CheckBox { Content = "The indicator must be polled (sends weight only on request)", IsChecked = _profile.Poll.Enabled };
        var command = new TextBox { Text = _profile.Poll.Command, Width = 160 };
        var interval = new TextBox { Text = _profile.Poll.IntervalMs.ToString(CultureInfo.InvariantCulture), Width = 90 };
        void Apply() => _profile = _profile with
        {
            Poll = new PollSettings
            {
                Enabled = poll.IsChecked == true, Command = command.Text,
                IntervalMs = int.TryParse(interval.Text, out var ms) ? ms : _profile.Poll.IntervalMs,
            },
        };
        poll.Click += (_, _) => Apply();
        command.LostFocus += (_, _) => Apply();
        interval.LostFocus += (_, _) => Apply();
        var row = new WrapPanel();
        row.Children.Add(Field("Request command (e.g. <ENQ>, R\\r\\n)", command));
        row.Children.Add(Field("Interval (ms)", interval));
        return new StackPanel { Children = { poll, row } };
    }

    private UIElement TcpPanel()
    {
        var host = new TextBox { Text = _profile.Connection.Tcp.Host, Width = 220 };
        var port = new TextBox { Text = _profile.Connection.Tcp.Port.ToString(CultureInfo.InvariantCulture), Width = 90 };
        void Apply() => _profile = _profile with { Connection = _profile.Connection with { Tcp = new TcpSettings { Host = host.Text.Trim(), Port = int.TryParse(port.Text, out var n) ? n : 0 } } };
        host.LostFocus += (_, _) => Apply();
        port.LostFocus += (_, _) => Apply();
        var row = new WrapPanel();
        row.Children.Add(Field("Host or IP address (serial-to-Ethernet converter)", host));
        row.Children.Add(Field("TCP port", port));
        return new StackPanel { Children = { row, PollPanel() } };
    }

    private UIElement ReplayPanel()
    {
        var file = new TextBox { Text = _profile.Connection.ReplayFile ?? string.Empty, Width = 460, IsReadOnly = true };
        var browse = new Button { Content = "Choose capture file…" };
        browse.Click += (_, _) =>
        {
            var path = Dialogs.OpenFile("Raw capture (*.wbcap)|*.wbcap|All files|*.*", App.Host.Paths.CaptureDirectory);
            if (path is null) return;
            file.Text = path;
            _profile = _profile with { Connection = _profile.Connection with { ReplayFile = path } };
        };
        var p = new StackPanel();
        p.Children.Add(Para("Replays a raw capture saved earlier (from this wizard or the raw data monitor). Use it to configure the parser for an indicator that is not connected to this PC."));
        p.Children.Add(Field("Capture file", new StackPanel { Orientation = Orientation.Horizontal, Children = { file, new Border { Width = 8 }, browse } }));
        return p;
    }

    private UIElement SimulatorPanel()
    {
        var template = new TextBox { Text = _profile.Connection.Simulator.Template, Width = 420 };
        template.LostFocus += (_, _) => _profile = _profile with { Connection = _profile.Connection with { Simulator = _profile.Connection.Simulator with { Template = template.Text } } };
        var p = new StackPanel();
        p.Children.Add(Para("The built-in simulator produces realistic indicator output for training and testing. Weights recorded with it are labelled SIMULATOR everywhere."));
        p.Children.Add(Field("Output template ({status}, {mode}, {weight:+000000.0}, {unit}, or @toledo / @toledo+cs)", template));
        return p;
    }

    private void Update(Func<SerialSettings, SerialSettings> change) =>
        _profile = _profile with { Connection = _profile.Connection with { Serial = change(_profile.Connection.Serial) } };

    // ------------------------------------------------------------------ step 2: raw data and framing

    private UIElement RawStep()
    {
        var p = new StackPanel();
        p.Children.Add(Para("Capture the raw output for a few seconds (ideally while the weight changes), then let the software suggest how messages are separated."));
        var view = new TextBox { IsReadOnly = true, FontFamily = (FontFamily)FindResource("Font.Mono"), Height = 220, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top };
        var hex = new CheckBox { Content = "Show as hexadecimal" };
        var status = new TextBlock { Style = St("Text.Muted") };
        var start = new Button { Content = "Start capture", Style = St("Button.Primary") };
        var stop = new Button { Content = "Stop", IsEnabled = false };
        var clear = new Button { Content = "Clear" };
        var save = new Button { Content = "Save capture…" };
        void Render()
        {
            byte[] data;
            lock (_raw) data = _raw.TakeLast(4096).ToArray();
            view.Text = hex.IsChecked == true ? ByteText.HexDump(data) : ByteText.ToDisplay(data);
            view.ScrollToEnd();
            status.Text = $"{_raw.Count} bytes captured{(_service is null ? string.Empty : $" · {_service.Current.Connection}")}";
        }
        hex.Click += (_, _) => Render();
        start.Click += (_, _) =>
        {
            lock (_raw) _raw.Clear();
            StartService(_profile, onBytes: chunk => { lock (_raw) _raw.AddRange(chunk.Data); });
            start.IsEnabled = false;
            stop.IsEnabled = true;
        };
        stop.Click += (_, _) =>
        {
            StopService(resume: false);
            start.IsEnabled = true;
            stop.IsEnabled = false;
            Render();
        };
        clear.Click += (_, _) => { lock (_raw) _raw.Clear(); Render(); };
        save.Click += (_, _) => SaveCapture();
        _onTick = Render;
        _refresh.Start();
        p.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { start, stop, clear, save, hex } });
        p.Children.Add(status);
        p.Children.Add(view);

        var suggestions = new ListBox { Height = 120, Margin = new Thickness(0, 6, 0, 0) };
        var analyse = new Button { Content = "Suggest framing", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        analyse.Click += (_, _) =>
        {
            byte[] data;
            lock (_raw) data = _raw.ToArray();
            var list = FramingAnalyzer.Analyze(data);
            suggestions.ItemsSource = list.Select(s => new KeyValuePair<FramingSuggestion, string>(s, $"{s.Confidence:P0}  {s.Framing.Mode} {Describe(s.Framing)} – {s.Explanation}")).ToList();
            suggestions.DisplayMemberPath = "Value";
            if (list.Count == 0) Dialogs.Info("No framing could be recognised. Capture more data, or set the framing manually below.");
        };
        suggestions.SelectionChanged += (_, _) =>
        {
            if (suggestions.SelectedItem is KeyValuePair<FramingSuggestion, string> s) _profile = _profile with { Framing = s.Key.Framing };
        };
        p.Children.Add(analyse);
        p.Children.Add(suggestions);
        p.Children.Add(new TextBlock { Text = "Framing (advanced)", Style = St("Text.H2") });
        var editor = new RecordEditor(_profile.Framing);
        var apply = new Button { Content = "Apply framing", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        apply.Click += (_, _) =>
        {
            try { _profile = _profile with { Framing = editor.Build<FramingOptions>() }; editor.ShowError(null); }
            catch (FormatException e) { editor.ShowError(e.Message); }
        };
        p.Children.Add(editor);
        p.Children.Add(apply);
        return p;
    }

    private static string Describe(FramingOptions f) => f.Mode switch
    {
        FramingMode.Terminator => $"terminator {f.Terminator}",
        FramingMode.StartEnd => $"start {f.StartMarker} end {f.EndMarker}{(f.TrailerLength > 0 ? $" + {f.TrailerLength} byte(s)" : string.Empty)}",
        FramingMode.FixedLength => $"{f.FixedLength} bytes{(f.StartMarker is null ? string.Empty : $" from {f.StartMarker}")}",
        _ => $"idle gap {f.IdleGapMs} ms",
    };

    private void SaveCapture()
    {
        byte[] data;
        lock (_raw) data = _raw.ToArray();
        if (data.Length == 0) return;
        Directory.CreateDirectory(App.Host.Paths.CaptureDirectory);
        var path = Dialogs.SaveFile("Raw capture (*.wbcap)|*.wbcap", $"capture-{DateTime.Now:yyyyMMdd-HHmmss}.wbcap", App.Host.Paths.CaptureDirectory);
        if (path is null) return;
        using (var recorder = new RawCaptureRecorder(path, _profile.Connection.Serial.Summary, "Captured with the set-up wizard")) recorder.Record(data);
        Dialogs.Info($"Saved {data.Length} bytes to {path}");
    }

    // ------------------------------------------------------------------ step 3: data format

    private List<string> Frames()
    {
        byte[] data;
        lock (_raw) data = _raw.ToArray();
        var assembler = new FrameAssembler(_profile.Framing);
        var now = DateTimeOffset.UtcNow;
        return assembler.Feed(data, now).Concat(assembler.Flush(now.AddSeconds(5))).Select(f => Encoding.Latin1.GetString(f.Payload)).Where(t => t.Length > 0).ToList();
    }

    private UIElement FormatStep()
    {
        var p = new StackPanel();
        var frames = Frames();
        p.Children.Add(Para(frames.Count == 0
            ? "No complete messages were found in the captured data. Go back and capture data, or adjust the framing. You can still set the parser manually."
            : $"{frames.Count} messages found. Example: {ByteText.ToDisplay(Encoding.Latin1.GetBytes(frames[^1]))}"));
        var results = new TextBox { IsReadOnly = true, FontFamily = (FontFamily)FindResource("Font.Mono"), Height = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top };
        if (frames.Count > 0)
        {
            _analysis = FormatLearner.Analyze(frames);
            var a = _analysis;
            var info = new StringBuilder();
            info.AppendLine($"Message shape: {a.DominantShape} ({a.ShapeCoverage:P0} of messages)");
            info.AppendLine($"Detected unit: {a.DetectedUnit?.ToString() ?? "none"}  ·  suggested parser reads {a.SuggestedParseRate:P0} of the samples");
            foreach (var s in a.StatusCandidates) info.AppendLine($"Status field: {string.Join(", ", s.Values)}{(s.UnknownValues.Count > 0 ? $" (unknown: {string.Join(", ", s.UnknownValues)})" : string.Empty)}");
            foreach (var n in a.Notes) info.AppendLine("• " + n);
            p.Children.Add(new TextBlock { Text = info.ToString(), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) });
            var candidates = new ListBox { Height = 120 };
            candidates.ItemsSource = a.WeightCandidates.Select(c => new KeyValuePair<WeightCandidate, string>(c, $"score {c.Score:0.00}  number #{c.NumberIndex}  e.g. '{c.LatestExample}'  {c.Description}")).ToList();
            candidates.DisplayMemberPath = "Value";
            candidates.SelectionChanged += (_, _) =>
            {
                if (candidates.SelectedItem is not KeyValuePair<WeightCandidate, string> c) return;
                _profile = _profile with { Parser = a.SuggestedParser with { Mode = ParserMode.Auto, NumberIndex = a.WeightCandidates.Count > 1 ? c.Key.NumberIndex : -1 } };
                Test(results, frames);
            };
            p.Children.Add(new TextBlock { Text = "Which number is the weight?", Style = St("Text.H2") });
            p.Children.Add(candidates);
            var useSuggestion = new Button { Content = "Use the suggested parser", Style = St("Button.Primary"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
            useSuggestion.Click += (_, _) =>
            {
                _profile = _profile with { Parser = a.SuggestedParser };
                Test(results, frames);
            };
            p.Children.Add(useSuggestion);
        }
        p.Children.Add(new TextBlock { Text = "Result on the captured messages", Style = St("Text.H2") });
        p.Children.Add(results);
        p.Children.Add(new TextBlock { Text = "Parser (advanced: regular expression, delimited, fixed positions, protocol plug-ins)", Style = St("Text.H2") });
        var protocols = App.Host.Protocols.All.Select(x => x.Name).ToList();
        p.Children.Add(Para($"Available protocol decoders: {string.Join(", ", protocols)}. Set Mode = Protocol and Protocol name to use one."));
        var editor = new RecordEditor(_profile.Parser);
        var apply = new Button { Content = "Apply and test", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        apply.Click += (_, _) =>
        {
            try
            {
                _profile = _profile with { Parser = editor.Build<ParserOptions>() };
                editor.ShowError(null);
                Test(results, frames);
            }
            catch (Exception e) when (e is FormatException or ArgumentException) { editor.ShowError(e.Message); }
        };
        p.Children.Add(editor);
        p.Children.Add(apply);
        if (frames.Count > 0) Test(results, frames);
        return p;
    }

    private void Test(TextBox output, IReadOnlyList<string> frames)
    {
        try
        {
            var parser = new WeightParser(_profile.Parser, App.Host.Protocols);
            var now = DateTimeOffset.UtcNow;
            var results = frames.TakeLast(200).Select((f, i) => parser.Parse(RawFrame.FromText(f, now, i))).ToList();
            var ok = results.Count(r => r.Outcome == ParseOutcome.Parsed);
            var sb = new StringBuilder($"Parsed {ok} of {results.Count} messages ({(results.Count == 0 ? 0 : ok * 100 / results.Count)} %).{Environment.NewLine}");
            foreach (var r in results.TakeLast(20))
            {
                sb.Append(ByteText.ToDisplay(r.Frame?.Payload ?? [])).Append("  →  ");
                sb.AppendLine(r.Outcome == ParseOutcome.Parsed && r.Reading?.Weight is { } w ? $"{w.Format(WeightDisplayOptions.DefaultKg)} [{r.Reading.Flags}]" : $"{r.Outcome}: {r.Reason}");
            }
            output.Text = sb.ToString();
        }
        catch (ArgumentException e)
        {
            output.Text = "The parser settings are not valid: " + e.Message;
        }
    }

    // ------------------------------------------------------------------ step 4: live test and reference weight

    private UIElement LiveStep()
    {
        var p = new StackPanel();
        p.Children.Add(Para("The indicator is read live with the new settings. Put a known load on the weighbridge (or read the indicator display), enter the value shown on the indicator and confirm."));
        var weight = new TextBlock { FontSize = 54, FontWeight = FontWeights.Bold, FontFamily = new FontFamily("Bahnschrift SemiBold, Segoe UI") };
        var state = new TextBlock { FontSize = 16 };
        var counters = new TextBlock { Style = St("Text.Muted") };
        var frames = new TextBox { IsReadOnly = true, FontFamily = (FontFamily)FindResource("Font.Mono"), Height = 150, VerticalContentAlignment = VerticalAlignment.Top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        StartService(_profile, null);
        _onTick = () =>
        {
            if (_service is null) return;
            var s = _service.Current;
            var r = s.Latest;
            weight.Text = r?.Weight is { } w ? w.Format(WeightDisplayOptions.DefaultKg) : "------";
            state.Text = $"{s.Connection} · {(s.Stability.IsStable ? "STABLE" : s.Stability.Reason.ToString())}{(r is null ? string.Empty : $" · flags {r.Flags}")}{(s.NewestFrameInvalid ? " · NEWEST MESSAGE INVALID" : string.Empty)}";
            counters.Text = $"{s.FramesParsed} messages accepted, {s.FramesRejected} rejected{(s.LastRejectReason is { } why ? $" (last: {why})" : string.Empty)}{(s.LastError is { } err ? $" · {err}" : string.Empty)}";
            frames.Text = string.Join(Environment.NewLine, _service.RecentFrames(12).Select(f => $"{f.Frame.Display}  →  {(f.Result.Outcome == ParseOutcome.Parsed ? f.Result.Reading?.Value?.ToString(CultureInfo.InvariantCulture) : f.Result.Outcome + ": " + f.Result.Reason)}"));
        };
        _refresh.Start();
        p.Children.Add(weight);
        p.Children.Add(state);
        p.Children.Add(counters);
        p.Children.Add(frames);

        p.Children.Add(new TextBlock { Text = "Reference weight", Style = St("Text.H2") });
        var displayed = new TextBox { Width = 160 };
        var unit = Combo(new[] { WeightUnit.Kilogram, WeightUnit.Tonne }, WeightUnit.Kilogram);
        var matches = new ListBox { Height = 100 };
        var check = new Button { Content = "Compare", Style = St("Button.Primary") };
        var verdict = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        check.Click += (_, _) =>
        {
            if (!decimal.TryParse(displayed.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var value)) { verdict.Text = "Enter the number shown on the indicator."; return; }
            var latest = _service?.RecentFrames(1).LastOrDefault();
            var parsed = _service?.Current.Latest?.Weight;
            var expected = Weight.From(value, (WeightUnit)unit.SelectedItem!);
            if (parsed is { } got && got.ToGrams() == expected.ToGrams())
            {
                verdict.Text = $"✔ The decoded value {got.Format(WeightDisplayOptions.DefaultKg)} matches the indicator display. You can mark the profile as verified in step 5.";
                verdict.Foreground = (Brush)FindResource("Brush.Ok");
                _profile = _profile with { Verified = true, VerifiedAt = DateTimeOffset.UtcNow, VerifiedBy = User.Username };
                return;
            }
            verdict.Foreground = (Brush)FindResource("Brush.Danger");
            verdict.Text = $"✖ The decoded value ({parsed?.Format(WeightDisplayOptions.DefaultKg) ?? "none"}) does not match {expected.Format(WeightDisplayOptions.DefaultKg)}. Alternative interpretations of the latest message are listed; choose one to use it.";
            _profile = _profile with { Verified = false, VerifiedAt = null, VerifiedBy = null };
            if (_analysis is null || latest is null) return;
            var list = FormatLearner.MatchReference(_analysis, Encoding.Latin1.GetString(latest.Frame.Payload), value, (WeightUnit)unit.SelectedItem!);
            matches.ItemsSource = list.Select(m => new KeyValuePair<ReferenceMatch, string>(m, $"{m.Description} → {m.ParsedValue}")).ToList();
            matches.DisplayMemberPath = "Value";
        };
        matches.SelectionChanged += (_, _) =>
        {
            if (matches.SelectedItem is not KeyValuePair<ReferenceMatch, string> m) return;
            _profile = _profile with { Parser = m.Key.Parser, Verified = false };
            StartService(_profile, null);
            verdict.Text = "Parser changed. Compare again with the indicator display.";
        };
        p.Children.Add(new WrapPanel { Children = { Field("Value shown on the indicator", displayed), Field("Unit", unit, 120), new Border { Child = check, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 6) } } });
        p.Children.Add(verdict);
        p.Children.Add(matches);
        return p;
    }

    // ------------------------------------------------------------------ step 5: stability and save

    private UIElement SaveStep()
    {
        StopService(resume: false);
        var p = new StackPanel();
        p.Children.Add(new TextBlock { Text = "Stability", Style = St("Text.H2"), Margin = new Thickness(0) });
        p.Children.Add(Para("A weight is stable when it stays within the tolerance for the window (and, when the indicator reports it, the indicator's own stable flag is set). Typical tolerance: one indicator division."));
        var stability = new RecordEditor(_profile.Stability);
        p.Children.Add(stability);
        p.Children.Add(new TextBlock { Text = "Profile", Style = St("Text.H2") });
        var name = new TextBox { Text = _profile.Name, Width = 360 };
        var maker = new TextBox { Text = _profile.Manufacturer ?? string.Empty, Width = 220 };
        var model = new TextBox { Text = _profile.Model ?? string.Empty, Width = 220 };
        var notes = new TextBox { Text = _profile.Notes ?? string.Empty, Height = 60, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
        p.Children.Add(Field("Name", name));
        p.Children.Add(new WrapPanel { Children = { Field("Manufacturer", maker), Field("Model", model) } });
        p.Children.Add(Field("Notes", notes));
        p.Children.Add(new TextBlock
        {
            Text = _profile.Verified ? $"✔ Verified against the indicator display by {_profile.VerifiedBy} at {_profile.VerifiedAt?.ToLocalTime():yyyy-MM-dd HH:mm}."
                : "Not verified: compare against the indicator display in step 4 before using the profile for trade weighing.",
            Style = St(_profile.Verified ? "Text.Muted" : "Text.Error"), Margin = new Thickness(0, 10, 0, 0),
        });
        IndicatorProfile Build() => _profile with
        {
            Stability = stability.Build<StabilityOptions>(), Name = name.Text.Trim(), Manufacturer = Blank(maker.Text), Model = Blank(model.Text), Notes = Blank(notes.Text),
        };
        var save = new Button { Content = "Save and use this profile", Style = St("Button.Primary") };
        save.Click += async (_, _) => await Dialogs.RunAsync(async () =>
        {
            var reason = Dialogs.Prompt("Save indicator profile", "The profile is saved as a new version and the indicator restarts with it. Describe the change for the audit trail.", minLength: 3);
            if (reason is null) return;
            _profile = await App.Host.Profiles.SaveAsync(User, Build(), activate: true, reason);
            _paused = false;
            Dialogs.Info($"Profile '{_profile.Name}' version {_profile.Version} saved and activated.");
            Go(4);
        });
        var export = new Button { Content = "Export profile…" };
        export.Click += async (_, _) => await Dialogs.RunAsync(async () =>
        {
            var path = Dialogs.SaveFile("Indicator profile (*.wbprofile.json)|*.wbprofile.json", $"{Safe(name.Text)}.wbprofile.json", App.Host.Paths.ProfileDirectory);
            if (path is null) return;
            await File.WriteAllTextAsync(path, Build().ToJson());
            Dialogs.Info($"Saved {path}. Share it with other sites using the same indicator model; it must be verified again there.");
        });
        var import = new Button { Content = "Import profile…" };
        import.Click += async (_, _) => await Dialogs.RunAsync(async () =>
        {
            var path = Dialogs.OpenFile("Indicator profile (*.wbprofile.json;*.json)|*.wbprofile.json;*.json", App.Host.Paths.ProfileDirectory);
            if (path is null) return;
            _profile = await App.Host.Profiles.ImportAsync(User, await File.ReadAllTextAsync(path));
            Dialogs.Info($"Imported '{_profile.Name}'. Test it (steps 3–4) before using it.");
            Go(0);
        });
        p.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0), Children = { save, export, import } });
        return p;
    }

    // ------------------------------------------------------------------ indicator service for testing

    private void PauseLiveIndicator()
    {
        if (_paused) return;
        _paused = true;
        App.Host.Indicator.Stop();
        App.Log.LogInformation("Live indicator paused by the set-up wizard");
    }

    private void StartService(IndicatorProfile profile, Action<RawChunk>? onBytes)
    {
        StopService(resume: false);
        PauseLiveIndicator();
        try
        {
            var service = new IndicatorService(profile, App.Host.Transports, App.Host.Time, null, App.Host.Protocols);
            if (onBytes is not null) service.BytesReceived += (_, chunk) => onBytes(chunk);
            service.Start();
            _service = service;
        }
        catch (ArgumentException e)
        {
            Dialogs.Error("The settings cannot be used: " + e.Message);
        }
    }

    private void StopService(bool resume)
    {
        _refresh.Stop();
        _service?.Stop();
        _service?.Dispose();
        _service = null;
        if (resume && _paused)
        {
            _paused = false;
            _ = App.Host.Indicator.StartAsync();
        }
    }

    // ------------------------------------------------------------------ helpers

    private static TextBlock Para(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };

    private static ComboBox Combo<T>(IEnumerable<T> items, T selected) => new() { ItemsSource = items.ToList(), SelectedItem = selected, MinWidth = 90 };

    private static StackPanel Field(string label, FrameworkElement control, double width = double.NaN)
    {
        if (!double.IsNaN(width)) control.Width = width;
        var p = new StackPanel { Margin = new Thickness(0, 0, 12, 6) };
        p.Children.Add(new TextBlock { Text = label, Style = St("Text.Label") });
        p.Children.Add(control);
        System.Windows.Automation.AutomationProperties.SetName(control, label);
        return p;
    }

    private static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string Safe(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c)).Trim();

    public void Dispose() => StopService(resume: true);
}
