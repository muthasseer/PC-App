using System.Windows;
using System.Windows.Controls;
using Weighbridge.Core.Weighing;
using Weighbridge.Data;
using Weighbridge.Desktop.ViewModels;
using Weighbridge.Desktop.Windows;

namespace Weighbridge.Desktop.Views;

public partial class WeighingView : UserControl, IPage, IDisposable
{
    private readonly WeighingViewModel _vm = new();
    private bool _loaded;

    public WeighingView()
    {
        InitializeComponent();
        DataContext = _vm;
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WeighingViewModel.Direction)) SyncDirection();
        };
        SyncDirection();
    }

    public WeighingViewModel ViewModel => _vm;

    public async void OnShown()
    {
        if (!_loaded)
        {
            _loaded = true;
            await Infrastructure.Dialogs.RunAsync(_vm.LoadAsync);
        }
        else
        {
            await Infrastructure.Dialogs.RunAsync(_vm.RefreshListsAsync);
        }
        VehicleBox.Focus();
    }

    public void OnHidden() { }

    private void SyncDirection()
    {
        DirOut.IsChecked = _vm.Direction == TransactionDirection.Outbound;
        DirIn.IsChecked = _vm.Direction == TransactionDirection.Inbound;
    }

    private void OnDirection(object sender, RoutedEventArgs e) =>
        _vm.Direction = sender == DirIn ? TransactionDirection.Inbound : TransactionDirection.Outbound;

    private void OnPendingDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (PendingGrid.SelectedItem is TransactionEntity t) _vm.SelectPending(t);
    }

    private void OnRecentDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (RecentGrid.SelectedItem is TransactionEntity t) new TransactionWindow(t.Id).Show();
    }

    public void Dispose() => _vm.Dispose();
}
