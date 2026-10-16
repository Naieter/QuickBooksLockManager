using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using QBLockManager.Models;
using QBLockManager.Services;

namespace QBLockManager.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public SettingsWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        LoadSettings();
    }

    private void LoadSettings()
    {
        ServiceUrl.Text = _settings.LockServiceBaseUrl;
        ApiKey.Text = _settings.ApiKey;
        QBPath.Text = _settings.QuickBooksExePath;
        MultiFile.IsChecked = _settings.MultiFileMode;
        QBPassword.Password = _settings.QuickBooksPassword ?? "";
        QBPasswordVisible.Text = _settings.QuickBooksPassword ?? "";
        UserName.Text = _settings.UserName;
        DisplayName.Text = _settings.DisplayName;
        Email.Text = _settings.Email;
        SharedRoot.Text = _settings.SharedRootPath;
        HeartbeatInterval.Text = _settings.HeartbeatIntervalSeconds.ToString();

        WatchedFolders.Text = string.Join(Environment.NewLine,
            _settings.WatchedFolders.Select(f => f.Path));
        Recursive.IsChecked = _settings.WatchedFolders.FirstOrDefault()?.Recursive ?? false;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _settings.LockServiceBaseUrl = ServiceUrl.Text.Trim();
        _settings.ApiKey = ApiKey.Text.Trim();
        _settings.QuickBooksExePath = QBPath.Text.Trim();
        _settings.MultiFileMode = MultiFile.IsChecked == true;
        var pwd = ShowQBPassword.IsChecked == true ? QBPasswordVisible.Text : QBPassword.Password;
        _settings.QuickBooksPassword = string.IsNullOrWhiteSpace(pwd) ? null : pwd;
        _settings.UserName = UserName.Text.Trim().DefaultIfEmpty(Environment.UserName);
        _settings.DisplayName = string.IsNullOrWhiteSpace(DisplayName.Text) ? null : DisplayName.Text.Trim();
        _settings.Email = string.IsNullOrWhiteSpace(Email.Text) ? null : Email.Text.Trim();
        _settings.SharedRootPath = SharedRoot.Text.Trim();

        if (int.TryParse(HeartbeatInterval.Text, out var hb) && hb >= 10 && hb <= 60)
            _settings.HeartbeatIntervalSeconds = hb;

        var recursive = Recursive.IsChecked == true;
        _settings.WatchedFolders = WatchedFolders.Text
            .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => new WatchedFolder { Path = p, Recursive = recursive })
            .ToList();

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ShowQBPassword_Changed(object sender, RoutedEventArgs e)
    {
        if (ShowQBPassword.IsChecked == true)
        {
            QBPasswordVisible.Text = QBPassword.Password;
            QBPasswordVisible.Visibility = System.Windows.Visibility.Visible;
            QBPassword.Visibility = System.Windows.Visibility.Collapsed;
        }
        else
        {
            QBPassword.Password = QBPasswordVisible.Text;
            QBPassword.Visibility = System.Windows.Visibility.Visible;
            QBPasswordVisible.Visibility = System.Windows.Visibility.Collapsed;
        }
    }

    // Saves the URL and key currently shown above as qblm-connect.json. Opens in the shared
    // QBLockCert folder when it exists, so publishing to every workstation is one click.
    private void ExportProfile_Click(object sender, RoutedEventArgs e)
    {
        var url = ServiceUrl.Text.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            MessageBox.Show("Enter the Lock Service URL first.", "Export Connection File",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var certFolder = SharedConnectionFile.FindFolder(_settings.WatchedFolders.Select(f => f.Path));
        var dlg = new SaveFileDialog
        {
            Title = "Export Connection File",
            FileName = SharedConnectionFile.FileName,
            DefaultExt = ".json",
            Filter = "Connection file (*.json)|*.json",
            OverwritePrompt = true,
            InitialDirectory = certFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            SharedConnectionFile.Write(dlg.FileName, url, ApiKey.Text);
            MessageBox.Show(
                $"Saved:\n{dlg.FileName}\n\nThis file contains the server's access key, so treat it like a password. " +
                $"Placed in the shared QuickBooks folder under {SharedConnectionFile.FolderName}, workstations that can't reach the server switch to it automatically.",
                "Connection File Exported", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not save the file:\n{ex.Message}", "Export Failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void DropZone_DragEnter(object sender, DragEventArgs e)
    {
        e.Effects = IsConnectionFileDrag(e) ? DragDropEffects.Copy : DragDropEffects.None;
        DropZone.Background = e.Effects == DragDropEffects.Copy
            ? (System.Windows.Media.Brush)FindResource("Accent")
            : (System.Windows.Media.Brush)FindResource("Surface");
        e.Handled = true;
    }

    private void DropZone_DragLeave(object sender, DragEventArgs e)
    {
        DropZone.Background = (System.Windows.Media.Brush)FindResource("Surface");
    }

    private void DropZone_Drop(object sender, DragEventArgs e)
    {
        DropZone.Background = (System.Windows.Media.Brush)FindResource("Surface");
        if (!IsConnectionFileDrag(e)) return;
        var path = ((string[])e.Data.GetData(DataFormats.FileDrop))[0];
        ImportProfileFrom(path);
    }

    private void DropZone_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select Connection File",
            Filter = "Connection file (qblm-connect.json)|qblm-connect.json|JSON files (*.json)|*.json",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        };
        if (dlg.ShowDialog() == true)
            ImportProfileFrom(dlg.FileName);
    }

    private static bool IsConnectionFileDrag(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return false;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        return files.Length == 1 && files[0].EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }

    private void ImportProfileFrom(string path)
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
            ServiceUrl.Text = p.ServerUrl;
            ApiKey.Text = p.ApiKey;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not read file:\n{ex.Message}",
                "Import Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BrowseQB_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select QuickBooks Executable",
            Filter = "Executable (*.exe)|*.exe",
            InitialDirectory = @"C:\Program Files\Intuit"
        };
        if (dlg.ShowDialog() == true)
            QBPath.Text = dlg.FileName;
    }
}

internal static class StringExtensions
{
    public static string DefaultIfEmpty(this string s, string def) =>
        string.IsNullOrWhiteSpace(s) ? def : s;
}
