using System.Windows;
using QBLockManager.Models;
using QBLockManager.ViewModels;

namespace QBLockManager;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(AppSettings settings)
    {
        InitializeComponent();
        _vm = new MainViewModel(settings);
        DataContext = _vm;
        Loaded += async (_, _) => await _vm.RefreshAsync();
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_vm.IsShuttingDown) return;
        e.Cancel = true;
        // Stay on the UI thread (Dispatcher) so MessageBox.Show inside OnWindowClosingAsync works.
        Dispatcher.InvokeAsync(async () =>
        {
            bool proceed = await _vm.OnWindowClosingAsync();
            if (!proceed) return; // User clicked Cancel — keep the app open.
            _vm.Dispose();
            Application.Current.Shutdown();
        });
    }
}
