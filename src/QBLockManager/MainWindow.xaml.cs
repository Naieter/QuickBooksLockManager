using System.IO;
using System.Text.Json;
using System.Windows;
using QBLockManager.Models;
using QBLockManager.Services;
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

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        e.Effects = IsConnectionFileDrag(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (!IsConnectionFileDrag(e)) return;

        var path = ((string[])e.Data.GetData(DataFormats.FileDrop))[0];
        var profile = TryReadConnectionProfile(path);
        if (profile == null)
        {
            MessageBox.Show(
                "That file doesn't look like a valid connection file (qblm-connect.json).",
                "Import Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await _vm.ApplyConnectionProfileAsync(profile);
        MessageBox.Show("Connection settings updated.", "Connected",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static bool IsConnectionFileDrag(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return false;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        return files.Length == 1 && files[0].EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }

    private static ConnectionProfile? TryReadConnectionProfile(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var p = JsonSerializer.Deserialize<ConnectionProfile>(json, opts);
            return p != null && !string.IsNullOrWhiteSpace(p.ServerUrl) ? p : null;
        }
        catch { return null; }
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
