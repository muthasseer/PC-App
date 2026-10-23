using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Weighbridge.Core.Common;
using Weighbridge.Core.Indicators;
using Weighbridge.Core.Indicators.Framing;
using Weighbridge.Core.Units;
using Weighbridge.Devices.Indicator;
using Weighbridge.Devices.Transports;
using Weighbridge.Desktop.Infrastructure;
using Weighbridge.Desktop.Windows;

namespace Weighbridge.Desktop.Views;

public sealed record FrameRow(string Time, string Text, string Hex, string Result, string Value, bool Ok);

/// <summary>
/// Shows exactly what the indicator sends (ASCII with control characters, or hex) and how every message was decoded,
/// without interrupting weighing. Raw data can be recorded to a capture file for analysis or replay.
/// </summary>
public sealed class RawMonitorView : UserControl, IPage, IDisposable
{
    private readonly TextBox _stream = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top };
    private readonly CheckBox _hex = new() { Content = "Hexadecimal" };
    private readonly CheckBox _pause = new() { Content = "Pause display" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _record = new() { Content = "Record to file" };
    private readonly ObservableCollection<FrameRow> _frames = [];
    private readonly List<byte> _bytes = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly object _gate = new();
    private readonly List<FrameRow> _pendingRows = [];
    private IndicatorService? _service;
    private RawCaptureRecorder? _recorder;

    public RawMonitorView()
    {
        _stream.FontFamily = (FontFamily)FindResource("Font.Mono");
        var grid = new DataGrid { ItemsSource = _frames, Height = 260 };
        grid.Columns.Add(new DataGridTextColumn { Header = "Time", Binding = new Binding(nameof(FrameRow.Time)) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Message", Binding = new Binding(nameof(FrameRow.Text)), Width = new DataGridLength(2, DataGridLengthUnitType.Star), FontFamily = (FontFamily)FindResource("Font.Mono") });
        grid.Columns.Add(new DataGridTextColumn { Header = "Value", Binding = new Binding(nameof(FrameRow.Value)) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Result", Binding = new Binding(nameof(FrameRow.Result)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Hex", Binding = new Binding(nameof(FrameRow.Hex)), Width = new DataGridLength(2, DataGridLengthUnitType.Star), FontFamily = (FontFamily)FindResource("Font.Mono") });
        grid.LoadingRow += (_, e) =>
        {
            if (e.Row.Item is FrameRow { Ok: false }) e.Row.Foreground = (Brush)FindResource("Brush.Danger");
            else e.Row.ClearValue(ForegroundProperty);
        };

        var clear = new Button { Content = Loc.T("common.refresh", "Clear") };
        clear.Click += (_, _) =>
        {
            lock (_gate) _bytes.Clear();
            _frames.Clear();
        };
        _record.Click += (_, _) => ToggleRecording();
        _hex.Click += (_, _) => Render();
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8), Children = { clear, _record, _hex, _pause } };

        var tester = ParserTester();
        var title = new TextBlock { Text = Loc.T("nav.monitor", "Raw data monitor"), Style = (Style)FindResource("Text.H1") };
        var panel = new DockPanel { Margin = new Thickness(16) };
        foreach (var (e, dock) in new (UIElement, Dock)[] { (title, Dock.Top), (_status, Dock.Top), (bar, Dock.Top), (tester, Dock.Bottom), (grid, Dock.Bottom) })
        {
            DockPanel.SetDock(e, dock);
            panel.Children.Add(e);
        }
        panel.Children.Add(_stream);
        Content = panel;
        _timer.Tick += (_, _) => Render();
        App.Host.Indicator.ServiceChanged += OnServiceChanged;
    }

    public void OnShown()
    {
        Attach(App.Host.Indicator.Current);
        _timer.Start();
    }

    public void OnHidden() => _timer.Stop();

    private void OnServiceChanged(object? sender, IndicatorService? service) => Dispatcher.BeginInvoke(() => Attach(service));

    private void Attach(IndicatorService? service)
    {
        if (ReferenceEquals(service, _service)) return;
        if (_service is not null)
        {
            _service.BytesReceived -= OnBytes;
            _service.FrameDecoded -= OnFrame;
        }
        _service = service;
        if (service is null) return;
        service.BytesReceived += OnBytes;
        service.FrameDecoded += OnFrame;
    }

    private void OnBytes(object? sender, RawChunk chunk)
    {
        lock (_gate)
        {
            _recorder?.Record(chunk.Data);
            _bytes.AddRange(chunk.Data);
            if (_bytes.Count > 64 * 1024) _bytes.RemoveRange(0, _bytes.Count - 32 * 1024);
        }
    }

    private void OnFrame(object? sender, DecodedFrame f)
    {
        var r = f.Result;
        var row = new FrameRow(f.Frame.ReceivedAt.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture), f.Frame.Display, f.Frame.Hex,
            r.Outcome == ParseOutcome.Parsed ? $"OK {r.Reading?.Flags}{(f.Checksum.Expected is not null ? $" · checksum {(f.Checksum.Valid ? "valid" : "INVALID")}" : string.Empty)}" : $"{r.Outcome}: {r.Reason}",
            r.Reading?.Weight is { } w ? w.Format(WeightDisplayOptions.DefaultKg) : string.Empty, r.Outcome == ParseOutcome.Parsed);
        lock (_gate) _pendingRows.Add(row);
    }

    private void Render()
    {
        var s = App.Host.Indicator.Snapshot;
        _status.Text = $"{s.PortDescription ?? "No indicator"} · {s.Connection} · profile {s.ProfileName} v{s.ProfileVersion} · {s.FramesParsed} accepted, {s.FramesRejected} rejected"
                       + (s.LastRejectReason is { } why ? $" · last rejection: {why}" : string.Empty)
                       + (_recorder is { } rec ? $" · RECORDING {rec.BytesRecorded} bytes to {Path.GetFileName(rec.Path)}" : string.Empty);
        if (_pause.IsChecked == true) return;
        byte[] data;
        List<FrameRow> rows;
        lock (_gate)
        {
            data = _bytes.TakeLast(6000).ToArray();
            rows = [.. _pendingRows];
            _pendingRows.Clear();
        }
        _stream.Text = _hex.IsChecked == true ? ByteText.HexDump(data) : ByteText.ToDisplay(data);
        _stream.ScrollToEnd();
        foreach (var r in rows) _frames.Add(r);
        while (_frames.Count > 300) _frames.RemoveAt(0);
    }

    private void ToggleRecording()
    {
        lock (_gate)
        {
            if (_recorder is not null)
            {
                var path = _recorder.Path;
                _recorder.Dispose();
                _recorder = null;
                _record.Content = "Record to file";
                Dialogs.Info($"Saved {path}. It can be replayed (Indicator setup → Replay) or sent to support.");
                return;
            }
            Directory.CreateDirectory(App.Host.Paths.CaptureDirectory);
            var file = Path.Combine(App.Host.Paths.CaptureDirectory, $"capture-{DateTime.Now:yyyyMMdd-HHmmss}.wbcap");
            _recorder = new RawCaptureRecorder(file, App.Host.Indicator.Snapshot.PortDescription, $"Profile {App.Host.Indicator.Profile?.Name}");
            _record.Content = "Stop recording";
        }
    }

    /// <summary>Paste sample messages (one per line, escapes like \r or &lt;STX&gt; allowed) and see how the active profile decodes them.</summary>
    private UIElement ParserTester()
    {
        var input = new TextBox { Height = 90, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = (FontFamily)FindResource("Font.Mono"), VerticalContentAlignment = VerticalAlignment.Top,
            Text = "ST,GS,+001234.5kg\nUS,NT,00025.40\n12345 kg\nGross: 12500 kg\nWT=12500.0" };
        var output = new TextBox { Height = 90, IsReadOnly = true, FontFamily = (FontFamily)FindResource("Font.Mono"), VerticalContentAlignment = VerticalAlignment.Top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var run = new Button { Content = "Test with the active profile", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        run.Click += (_, _) =>
        {
            var profile = App.Host.Indicator.Profile;
            if (profile is null) { output.Text = "No indicator profile is active."; return; }
            var decoder = new IndicatorDecoder(profile, App.Host.Protocols);
            var sb = new StringBuilder();
            var now = DateTimeOffset.UtcNow;
            foreach (var line in input.Text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0))
            {
                var bytes = ByteText.Unescape(line);
                var d = decoder.DecodeFrame(new RawFrame(bytes, bytes, [], now, 0));
                sb.AppendLine($"{line}  →  {(d.Result.Outcome == ParseOutcome.Parsed ? $"{d.Result.Reading?.Weight?.Format(WeightDisplayOptions.DefaultKg)} [{d.Result.Reading?.Flags}]" : $"{d.Result.Outcome}: {d.Result.Reason}")}");
            }
            output.Text = sb.ToString();
        };
        var grid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new StackPanel { Children = { new TextBlock { Text = "Parser test – sample messages", Style = (Style)FindResource("Text.H2") }, input, run } };
        var right = new StackPanel { Children = { new TextBlock { Text = "Result", Style = (Style)FindResource("Text.H2") }, output } };
        Grid.SetColumn(right, 2);
        grid.Children.Add(left);
        grid.Children.Add(right);
        return grid;
    }

    public void Dispose()
    {
        _timer.Stop();
        App.Host.Indicator.ServiceChanged -= OnServiceChanged;
        Attach(null);
        lock (_gate) _recorder?.Dispose();
    }
}
