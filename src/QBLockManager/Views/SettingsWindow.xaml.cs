using System.IO;
using System.Windows;
using Microsoft.Win32;
using QBLockManager.Models;

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
        AdminApiKey.Text = _settings.AdminApiKey;
        AdminMode.IsChecked = _settings.IsAdminMode;
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
        _settings.AdminApiKey = AdminApiKey.Text.Trim();
        _settings.IsAdminMode = AdminMode.IsChecked == true;
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
