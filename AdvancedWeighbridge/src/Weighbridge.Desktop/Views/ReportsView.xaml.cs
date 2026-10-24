using System.Data;
using System.Windows;
using System.Windows.Controls;
using Weighbridge.Application.Reports;
using Weighbridge.Core.Security;
using Weighbridge.Desktop.Infrastructure;
using Weighbridge.Desktop.Windows;
using Weighbridge.Reporting.Reports;

namespace Weighbridge.Desktop.Views;

public partial class ReportsView : UserControl, IPage
{
    public ReportsView()
    {
        InitializeComponent();
        KindBox.ItemsSource = Enum.GetValues<ReportKind>().Select(k => new KeyValuePair<ReportKind, string>(k, ReportService.Title(k))).ToList();
        KindBox.DisplayMemberPath = "Value";
        KindBox.SelectedIndex = 1;
    }

    public void OnShown()
    {
        var user = App.Host.Session.Require();
        XlsxButton.IsEnabled = CsvButton.IsEnabled = PdfButton.IsEnabled = user.Has(Permissions.ReportsExport);
    }

    public void OnHidden() { }

    private ReportRequest Request() => new(((KeyValuePair<ReportKind, string>)KindBox.SelectedItem).Key, Filter.Build(1, 100_000));

    private async void OnPreview(object sender, RoutedEventArgs e) => await Dialogs.RunAsync(async () =>
    {
        var table = await App.Host.Reports.BuildAsync(App.Host.Session.Require(), Request());
        TitleText.Text = $"{table.Title} – {table.Subtitle}";
        var data = new DataTable();
        for (var i = 0; i < table.Columns.Count; i++) data.Columns.Add($"c{i}", typeof(string));
        foreach (var row in table.Rows.Take(5000)) data.Rows.Add(row.Select((v, i) => (object)ReportFormat.Cell(v, table.Columns[i].Kind, table)).ToArray());
        Grid.Columns.Clear();
        for (var i = 0; i < table.Columns.Count; i++)
        {
            var col = table.Columns[i];
            var column = new DataGridTextColumn { Header = col.Header, Binding = new System.Windows.Data.Binding($"c{i}") };
            if (ReportFormat.RightAligned(col.Kind)) column.ElementStyle = new Style(typeof(TextBlock)) { Setters = { new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right) } };
            Grid.Columns.Add(column);
        }
        Grid.ItemsSource = data.DefaultView;
        TotalsText.Text = table.Totals is null ? $"{table.Rows.Count} rows" :
            $"{table.Rows.Count} rows · totals: " + string.Join("  ", table.Totals.Select((v, i) => v is null ? null : $"{table.Columns[i].Header} {ReportFormat.Cell(v, table.Columns[i].Kind, table)}").Where(x => x is not null));
        if (table.Rows.Count > 5000) TotalsText.Text += " (preview shows the first 5 000 rows; exports contain all rows)";
    });

    private Task ExportAsync(ExportFormat format) => Dialogs.RunAsync(async () =>
    {
        var report = await App.Host.Reports.ExportAsync(App.Host.Session.Require(), Request(), format);
        var ext = Path.GetExtension(report.FileName);
        var path = Dialogs.SaveFile($"{format} (*{ext})|*{ext}", report.FileName, App.Host.Paths.ExportDirectory);
        if (path is null) return;
        await File.WriteAllBytesAsync(path, report.Content);
        Dialogs.Info($"{report.Rows} rows exported to {path}");
    });

    private async void OnXlsx(object sender, RoutedEventArgs e) => await ExportAsync(ExportFormat.Xlsx);

    private async void OnCsv(object sender, RoutedEventArgs e) => await ExportAsync(ExportFormat.Csv);

    private async void OnPdf(object sender, RoutedEventArgs e) => await ExportAsync(ExportFormat.Pdf);
}
