using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using QBLockManager.Models;
using QBLockManager.Services;

namespace QBLockManager.Views;

public partial class FirstRunWindow : Window
{
    private bool _allowClose = false;
    private string? _detectedQbExePath;
    private ConnectionProfile? _profile;

    public AppSettings? ConfiguredSettings { get; private set; }

    public FirstRunWindow(ConnectionProfile? profile)
    {
        _profile = profile;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_profile != null)
        {
            BannerProfileLoaded.Visibility = Visibility.Visible;
            DropZone.Visibility = Visibility.Collapsed;
            TxtManualToggle.Visibility = Visibility.Collapsed;
        }

        // Silently auto-detect QuickBooks — only bother the user if we can't find it.
        _detectedQbExePath = QuickBooksLauncher.FindQuickBooksExe();
        if (_detectedQbExePath == null)
        {
            TxtQBDetectStatus.Text = "Note: QuickBooks wasn't found automatically. You can set its location later in Settings.";
            TxtQBDetectStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
        }
    }

    // ── Connection: drop zone ──────────────────────────────────────────────

    private void DropZone_DragEnter(object sender, DragEventArgs e)
    {
        e.Effects = IsConnectionFileDrag(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void DropZone_DragLeave(object sender, DragEventArgs e) { }

    private void DropZone_Drop(object sender, DragEventArgs e)
    {
        if (!IsConnectionFileDrag(e)) return;
        var path = ((string[])e.Data.GetData(DataFormats.FileDrop))[0];
        ApplyProfileFile(path);
    }

    private void DropZone_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Connection File",
            Filter = "Connection file (qblm-connect.json)|qblm-connect.json|JSON files (*.json)|*.json",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        };
        if (dlg.ShowDialog() == true)
            ApplyProfileFile(dlg.FileName);
    }

    private static bool IsConnectionFileDrag(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return false;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        return files.Length == 1 && files[0].EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyProfileFile(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var p = JsonSerializer.Deserialize<ConnectionProfile>(json, opts);
            if (p == null || string.IsNullOrWhiteSpace(p.ServerUrl))
            {
                MessageBox.Show("That file doesn't contain valid connection settings.",
                    "Import Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _profile = p;
            BannerProfileLoaded.Visibility = Visibility.Visible;
            DropZone.Visibility = Visibility.Collapsed;
            TxtManualToggle.Visibility = Visibility.Collapsed;
            PanelManual.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not read file:\n{ex.Message}",
                "Import Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ManualToggle_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        PanelManual.Visibility = Visibility.Visible;
        DropZone.Visibility = Visibility.Collapsed;
        TxtManualToggle.Visibility = Visibility.Collapsed;
    }

    // ── QB folder ────────────────────────────────────────────────────────

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select the folder where your QuickBooks (.QBW) files are stored"
        };
        if (dialog.ShowDialog() == true)
            TxtQBFolder.Text = dialog.FolderName;
    }

    // ── Finish ───────────────────────────────────────────────────────────

    private void Finish_Click(object sender, RoutedEventArgs e)
    {
        var serverUrl = _profile?.ServerUrl ?? TxtServerUrl.Text.Trim();
        var apiKey = _profile?.ApiKey ?? TxtApiKey.Text.Trim();

        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            MessageBox.Show("Please provide the connection file, or enter the server address manually.",
                "Connection Required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(TxtQBFolder.Text))
        {
            MessageBox.Show("Please select the folder where your QuickBooks files are stored.",
                "Folder Required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var folder = TxtQBFolder.Text.Trim();
        var settings = new AppSettings
        {
            SetupCompleted = true,
            LockServiceBaseUrl = NormalizeServerUrl(serverUrl),
            ApiKey = apiKey,
            QuickBooksExePath = _detectedQbExePath ?? new AppSettings().QuickBooksExePath,
            UserName = Environment.UserName,
            DisplayName = FormatWindowsName(Environment.UserName),
            SharedRootPath = folder,
            MultiFileMode = false,
            HeartbeatIntervalSeconds = 20,
            ServiceTimeoutSeconds = 10,
            WatchedFolders = new List<WatchedFolder>
            {
                new() { Path = folder, Recursive = false, Label = "QuickBooks Files" }
            }
        };

        new SettingsService().Save(settings);
        ConfiguredSettings = settings;

        _allowClose = true;
        DialogResult = true;
        Close();
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose) return;
        var r = MessageBox.Show(
            "Setup is not complete. The Lock Manager requires setup before it can be used.\n\nExit anyway?",
            "Setup Incomplete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (r == MessageBoxResult.No) e.Cancel = true;
        else Application.Current.Shutdown();
    }

    private static string NormalizeServerUrl(string url)
    {
        url = url.Trim().TrimEnd('/');
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = "http://" + url;
        return url;
    }

    private static string FormatWindowsName(string windowsUser)
    {
        if (windowsUser.Contains('.'))
        {
            var parts = windowsUser.Split('.');
            return string.Join(" ", parts.Select(p =>
                p.Length > 0 ? char.ToUpper(p[0]) + p[1..] : p));
        }
        return windowsUser;
    }
}
