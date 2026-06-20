using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using QBLockManager.Models;
using QBLockManager.Services;

namespace QBLockManager.Views;

public partial class SetupWizardWindow : Window
{
    private int _step = 1;
    private const int TotalSteps = 5;
    private bool _allowClose = false;

    public AppSettings? ConfiguredSettings { get; private set; }

    // Panels in order
    private System.Windows.Controls.StackPanel[] _panels = null!;

    private static readonly (string Title, string Subtitle)[] StepInfo =
    [
        ("Welcome",               "Let's get your workstation set up."),
        ("Lock Server",           "Where is the lock server on your network?"),
        ("Your QuickBooks Files", "Where are your company files stored?"),
        ("About You",             "How should you appear to other users?"),
        ("QuickBooks Location",   "Where is QuickBooks installed?"),
    ];

    public SetupWizardWindow()
    {
        InitializeComponent();
        _panels = [PanelStep1, PanelStep2, PanelStep3, PanelStep4, PanelStep5];
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Pre-fill display name from Windows
        TxtDisplayName.Text = FormatWindowsName(Environment.UserName);

        // Try to auto-detect QuickBooks
        var qbPath = QuickBooksLauncher.FindQuickBooksExe();
        if (qbPath != null)
        {
            TxtQBExePath.Text = qbPath;
            TxtQBDetectStatus.Text = "✓ We found QuickBooks on this computer. Confirm the path below, or click Browse to choose a different version.";
            TxtQBDetectStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
        }
        else
        {
            TxtQBDetectStatus.Text = "QuickBooks was not found automatically. Use Browse to locate QBW32.EXE.";
            TxtQBDetectStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
            PanelQBNotFound.Visibility = Visibility.Visible;
        }

        RefreshStepUI();
    }

    // ── Navigation ──────────────────────────────────────────────────────────

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateCurrentStep()) return;
        if (_step < TotalSteps)
        {
            _step++;
            if (_step == TotalSteps) BuildSummary();
            RefreshStepUI();
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_step > 1) { _step--; RefreshStepUI(); }
    }

    private void Finish_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateCurrentStep()) return;
        SaveSettings();
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

    // ── Validation ───────────────────────────────────────────────────────────

    private bool ValidateCurrentStep()
    {
        switch (_step)
        {
            case 2: // Server
                if (string.IsNullOrWhiteSpace(TxtServerUrl.Text))
                {
                    Shake(TxtServerUrl);
                    ShowError("Please enter the lock server address.", TxtConnectionResult);
                    return false;
                }
                // Silently accept bare host:port — NormalizeUrl in LockServiceClient will prepend http://
                if (string.IsNullOrWhiteSpace(TxtApiKey.Text))
                {
                    Shake(TxtApiKey);
                    ShowError("Please enter the access key.", TxtConnectionResult);
                    return false;
                }
                return true;

            case 3: // Folder
                if (string.IsNullOrWhiteSpace(TxtQBFolder.Text))
                {
                    MessageBox.Show("Please browse to select your QuickBooks files folder.",
                        "Folder Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
                return true;

            case 4: // Name
                if (string.IsNullOrWhiteSpace(TxtDisplayName.Text))
                {
                    Shake(TxtDisplayName);
                    return false;
                }
                return true;

            case 5: // QB path
                if (string.IsNullOrWhiteSpace(TxtQBExePath.Text) ||
                    !File.Exists(TxtQBExePath.Text))
                {
                    MessageBox.Show(
                        "QuickBooks executable was not found at the path shown.\n\nClick Browse to locate QBW32.EXE manually.",
                        "QuickBooks Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
                return true;

            default:
                return true;
        }
    }

    // ── Step 2: Test Connection ───────────────────────────────────────────────

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        BtnTestConnection.IsEnabled = false;
        TxtConnectionResult.Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));
        TxtConnectionResult.Text = "Connecting…";

        try
        {
            var raw = TxtServerUrl.Text.Trim();
            var baseUrl = (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                           raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                ? raw.TrimEnd('/')
                : "http://" + raw.TrimEnd('/');
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            client.DefaultRequestHeaders.Add("X-Api-Key", TxtApiKey.Text.Trim());

            // Hit an authenticated endpoint — /health skips auth, so a wrong key
            // would falsely show as success.
            var resp = await client.GetAsync(baseUrl + "/api/files/locks");

            if (resp.IsSuccessStatusCode)
            {
                TxtConnectionResult.Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
                TxtConnectionResult.Text = "✓ Connected and authenticated!";
            }
            else if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                TxtConnectionResult.Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
                TxtConnectionResult.Text = "✗ Server is reachable but the access key is wrong. Double-check the key.";
            }
            else
            {
                TxtConnectionResult.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
                TxtConnectionResult.Text = $"✗ Server responded with error {(int)resp.StatusCode}.";
            }
        }
        catch (TaskCanceledException)
        {
            TxtConnectionResult.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
            TxtConnectionResult.Text = "✗ Could not reach server. Check the address and your network.";
        }
        catch (Exception ex)
        {
            TxtConnectionResult.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
            TxtConnectionResult.Text = $"✗ {ex.Message}";
        }
        finally
        {
            BtnTestConnection.IsEnabled = true;
        }
    }

    // ── Step 3: Browse folder ─────────────────────────────────────────────────

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select the folder where your QuickBooks (.QBW) files are stored"
        };

        if (dialog.ShowDialog() == true)
        {
            TxtQBFolder.Text = dialog.FolderName;
            ScanFolder(dialog.FolderName, ChkRecursive.IsChecked == true);
        }
    }

    private void ChkRecursive_Changed(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(TxtQBFolder.Text))
            ScanFolder(TxtQBFolder.Text, ChkRecursive.IsChecked == true);
    }

    private void ScanFolder(string folder, bool recursive)
    {
        try
        {
            var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var files = Directory.GetFiles(folder, "*.QBW", option);

            if (files.Length > 0)
            {
                TxtFoundCount.Text = $"✓ Found {files.Length} QuickBooks file{(files.Length == 1 ? "" : "s")}:";
                FoundFilesList.ItemsSource = files.Select(f => System.IO.Path.GetFileName(f)).ToList();
                PanelFoundFiles.Visibility = Visibility.Visible;
                TxtNoFiles.Visibility = Visibility.Collapsed;
            }
            else
            {
                PanelFoundFiles.Visibility = Visibility.Collapsed;
                TxtNoFiles.Visibility = Visibility.Visible;
            }
        }
        catch
        {
            PanelFoundFiles.Visibility = Visibility.Collapsed;
            TxtNoFiles.Visibility = Visibility.Collapsed;
        }
    }

    // ── Step 5: Browse QB exe ─────────────────────────────────────────────────

    private void BrowseQBExe_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select QuickBooks Executable (QBW32.EXE)",
            Filter = "QuickBooks (QBW32.EXE)|QBW32.EXE|All Executables (*.exe)|*.exe",
            InitialDirectory = Directory.Exists(@"C:\Program Files\Intuit")
                ? @"C:\Program Files\Intuit"
                : @"C:\Program Files"
        };
        if (dlg.ShowDialog() == true)
        {
            TxtQBExePath.Text = dlg.FileName;
            PanelQBNotFound.Visibility = Visibility.Collapsed;
        }
    }

    // ── Summary (shown when reaching step 5) ─────────────────────────────────

    private void BuildSummary()
    {
        TxtSummaryServer.Text = $"  Lock server: {TxtServerUrl.Text}";
        TxtSummaryFolder.Text = $"  QB files folder: {TxtQBFolder.Text}";
        TxtSummaryName.Text   = $"  Your name: {TxtDisplayName.Text}";
        TxtSummaryQB.Text     = $"  QuickBooks: {System.IO.Path.GetFileName(TxtQBExePath.Text)}";
        PanelAllSet.Visibility = File.Exists(TxtQBExePath.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Save ──────────────────────────────────────────────────────────────────

    private void SaveSettings()
    {
        var folder = TxtQBFolder.Text.Trim();
        var settings = new AppSettings
        {
            SetupCompleted       = true,
            LockServiceBaseUrl   = NormalizeServerUrl(TxtServerUrl.Text),
            ApiKey               = TxtApiKey.Text.Trim(),
            QuickBooksExePath    = TxtQBExePath.Text.Trim(),
            UserName             = Environment.UserName,
            DisplayName          = TxtDisplayName.Text.Trim(),
            Email                = TxtEmail.Text.Trim(),
            SharedRootPath       = folder,
            MultiFileMode        = false,
            HeartbeatIntervalSeconds = 20,
            ServiceTimeoutSeconds    = 10,
            WatchedFolders = new List<WatchedFolder>
            {
                new() { Path = folder, Recursive = ChkRecursive.IsChecked == true, Label = "QuickBooks Files" }
            }
        };

        new SettingsService().Save(settings);
        ConfiguredSettings = settings;
    }

    // ── UI helpers ────────────────────────────────────────────────────────────

    private static readonly SolidColorBrush ActiveDot   = new(Color.FromRgb(0x63, 0x66, 0xF1));
    private static readonly SolidColorBrush CompleteDot = new(Color.FromRgb(0x22, 0xC5, 0x5E));
    private static readonly SolidColorBrush InactiveDot = new(Color.FromRgb(0x3F, 0x3F, 0x5C));

    private void RefreshStepUI()
    {
        // Hide all panels, show current one
        for (int i = 0; i < _panels.Length; i++)
            _panels[i].Visibility = i + 1 == _step ? Visibility.Visible : Visibility.Collapsed;

        // Update header text
        StepTitle.Text    = StepInfo[_step - 1].Title;
        StepSubtitle.Text = StepInfo[_step - 1].Subtitle;

        // Update progress dots
        var dots = new[] { Dot1, Dot2, Dot3, Dot4, Dot5 };
        for (int i = 0; i < dots.Length; i++)
        {
            dots[i].Fill = i + 1 < _step ? CompleteDot
                         : i + 1 == _step ? ActiveDot
                         : InactiveDot;
            dots[i].Width  = i + 1 == _step ? 13 : 10;
            dots[i].Height = i + 1 == _step ? 13 : 10;
        }

        // Back button
        BtnBack.Visibility = _step > 1 ? Visibility.Visible : Visibility.Collapsed;

        // Next vs Finish
        bool isLast = _step == TotalSteps;
        BtnNext.Visibility   = isLast ? Visibility.Collapsed : Visibility.Visible;
        BtnFinish.Visibility = isLast ? Visibility.Visible   : Visibility.Collapsed;
    }

    private static void Shake(System.Windows.Controls.TextBox box)
    {
        box.BorderBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
        box.Focus();
    }

    private static void ShowError(string msg, System.Windows.Controls.TextBlock target)
    {
        target.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
        target.Text = msg;
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
        // "john.smith" → "John Smith"
        if (windowsUser.Contains('.'))
        {
            var parts = windowsUser.Split('.');
            return string.Join(" ", parts.Select(p =>
                p.Length > 0 ? char.ToUpper(p[0]) + p[1..] : p));
        }
        return windowsUser;
    }
}
