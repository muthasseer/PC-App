using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Weighbridge.Application.Reports;
using Weighbridge.Data;
using Weighbridge.Desktop.Infrastructure;
using Weighbridge.Desktop.Windows;

namespace Weighbridge.Desktop.Views;

public partial class HistoryView : UserControl, IPage
{
    private int _page = 1;
    private long _total;
    private const int PageSize = 100;

    public HistoryView() => InitializeComponent();

    public async void OnShown() => await Dialogs.RunAsync(LoadAsync);

    public void OnHidden() { }

    private async Task LoadAsync()
    {
        var result = await App.Host.History.SearchAsync(App.Host.Session.Require(), Filter.Build(_page, PageSize));
        Grid.ItemsSource = result.Items;
        _total = result.Total;
        var pages = Math.Max(1, (int)Math.Ceiling(_total / (double)PageSize));
        SummaryText.Text = $"{_total} transactions · page {_page} of {pages} · net of this page {GramsConverter.Display.Format(Core.Units.Weight.FromGrams(result.Items.Sum(t => t.NetG ?? 0)))}";
        PrevButton.IsEnabled = _page > 1;
        NextButton.IsEnabled = _page < pages;
    }

    private async void OnSearch(object sender, RoutedEventArgs e)
    {
        _page = 1;
        await Dialogs.RunAsync(LoadAsync);
    }

    private async void OnPrevious(object sender, RoutedEventArgs e)
    {
        _page--;
        await Dialogs.RunAsync(LoadAsync);
    }

    private async void OnNext(object sender, RoutedEventArgs e)
    {
        _page++;
        await Dialogs.RunAsync(LoadAsync);
    }

    private void OnOpen(object sender, MouseButtonEventArgs e) => Open();

    private void OnGridKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Open();
            e.Handled = true;
        }
    }

    private void Open()
    {
        if (Grid.SelectedItem is TransactionEntity t) new TransactionWindow(t.Id).Show();
    }

    private Task ExportAsync(ExportFormat format) => Dialogs.RunAsync(async () =>
    {
        var report = await App.Host.Reports.ExportAsync(App.Host.Session.Require(), new ReportRequest(ReportKind.Transactions, Filter.Build(1, 100_000)), format);
        var path = Dialogs.SaveFile($"{format} (*.{Path.GetExtension(report.FileName).TrimStart('.')})|*{Path.GetExtension(report.FileName)}", report.FileName, App.Host.Paths.ExportDirectory);
        if (path is null) return;
        await File.WriteAllBytesAsync(path, report.Content);
        Dialogs.Info($"{report.Rows} rows exported to {path}");
    });

    private async void OnExportXlsx(object sender, RoutedEventArgs e) => await ExportAsync(ExportFormat.Xlsx);

    private async void OnExportCsv(object sender, RoutedEventArgs e) => await ExportAsync(ExportFormat.Csv);

    private async void OnExportPdf(object sender, RoutedEventArgs e) => await ExportAsync(ExportFormat.Pdf);
}
