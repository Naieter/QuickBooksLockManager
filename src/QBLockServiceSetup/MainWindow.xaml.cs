using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace QBLockServiceSetup;

public partial class MainWindow : Window
{
    private const string ServiceName = "QBLockService";

    public MainWindow()
    {
        InitializeComponent();
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        if (v != null)
            VersionText.Text = v.Build > 0 ? $"v{v.Major}.{v.Minor}.{v.Build}" : $"v{v.Major}.{v.Minor}";
        GenerateKeys_Click(this, null!);
        CheckCurrentInstall();
    }

    private void CheckCurrentInstall()
    {
        var svc = GetService();
        if (svc != null)
        {
            SetBanner($"Service already installed — Status: {svc.Status}", "#166534", "#bbf7d0");
            InstallButton.Content = "Update / Reinstall";
            UninstallButton.Visibility = Visibility.Visible;

            // Pre-fill from existing config
            var cfgPath = Path.Combine(InstallDirBox.Text, "appsettings.json");
            if (File.Exists(cfgPath))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(cfgPath));
                    var root = doc.RootElement;
                    if (root.TryGetProperty("ApiKey", out var ak) && ak.GetString() is string a)
                        ApiKeyBox.Text = a;
                    if (root.TryGetProperty("Urls", out var urls) && urls.GetString() is string u)
                    {
                        var port = u.Split(':').LastOrDefault()?.Trim('/') ?? "5100";
                        PortBox.Text = port;
                    }
                    if (root.TryGetProperty("Database", out var db) &&
                        db.TryGetProperty("Path", out var dbp) && dbp.GetString() is string dbPath)
                        InstallDirBox.Text = Path.GetDirectoryName(Path.GetDirectoryName(dbPath)) ?? InstallDirBox.Text;
                    if (root.TryGetProperty("LockSettings", out var ls) &&
                        ls.TryGetProperty("TimeoutMinutes", out var tm))
                        TimeoutBox.Text = tm.GetInt32().ToString();
                }
                catch { }
            }
        }
        else
        {
            SetBanner("Not installed — enter settings and click Install.", "#1e3a5f", "#93c5fd");
        }
    }

    private void SetBanner(string text, string bg, string fg)
    {
        BannerBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bg));
        BannerText.Text = text;
        BannerText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fg));
    }

    private void GenerateKeys_Click(object sender, RoutedEventArgs e)
    {
        ApiKeyBox.Text = GenerateKey();
    }

    private static string GenerateKey()
    {
        // Generate until we have at least 40 alphanumeric characters.
        // 32 bytes → ~40.3 alphanumeric chars on average, so retry is rare.
        while (true)
        {
            var bytes = RandomNumberGenerator.GetBytes(48);
            var key = Convert.ToBase64String(bytes)
                .Replace("+", "").Replace("/", "").Replace("=", "");
            if (key.Length >= 40) return key[..40];
        }
    }

    private void Install_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateInputs()) return;

        InstallButton.IsEnabled = false;
        LogText.Text = "";

        try
        {
            var installDir  = InstallDirBox.Text.Trim();
            var apiKey      = ApiKeyBox.Text.Trim();
            var port        = PortBox.Text.Trim();
            var timeout     = int.Parse(TimeoutBox.Text.Trim());
            var dbPath      = Path.Combine(installDir, "data", "qblocks.db");

            // Stop service if running
            var svc = GetService();
            if (svc?.Status == ServiceControllerStatus.Running)
            {
                Log("Stopping existing service...");
                svc.Stop();
                svc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
            }

            // Create directories
            Directory.CreateDirectory(installDir);
            Directory.CreateDirectory(Path.Combine(installDir, "data"));
            Log($"Install directory: {installDir}");

            // Extract embedded QBLockService.exe
            var exePath = Path.Combine(installDir, "QBLockService.exe");
            Log("Extracting QBLockService.exe...");
            using (var stream = GetType().Assembly.GetManifestResourceStream("QBLockService.exe")
                   ?? throw new InvalidOperationException("Embedded QBLockService.exe not found."))
            using (var fs = File.Create(exePath))
                stream.CopyTo(fs);
            Log($"Extracted to: {exePath}");

            // Write appsettings.json
            var settings = new
            {
                Logging = new { LogLevel = new { Default = "Information", Microsoft_AspNetCore = "Warning" } },
                AllowedHosts = "*",
                Urls         = $"http://0.0.0.0:{port}",
                ApiKey       = apiKey,
                Database     = new { Path = dbPath.Replace("\\", "/") },
                LockSettings = new { TimeoutMinutes = timeout, StaleCheckIntervalSeconds = 60 }
            };
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            // Fix property name Microsoft_AspNetCore → Microsoft.AspNetCore
            json = json.Replace("\"Microsoft_AspNetCore\"", "\"Microsoft.AspNetCore\"");
            File.WriteAllText(Path.Combine(installDir, "appsettings.json"), json);
            Log("Config written.");

            // Install or update Windows Service
            if (svc == null)
            {
                Log("Creating Windows Service...");
                RunSc($"create {ServiceName} binpath= \"{exePath}\" start= auto DisplayName= \"QuickBooks Lock Service\"");
                RunSc($"description {ServiceName} \"Central lock manager for QuickBooks company files.\"");
            }
            else
            {
                Log("Updating service binary path...");
                RunSc($"config {ServiceName} binpath= \"{exePath}\"");
            }

            // Recovery: restart on failure
            RunSc($"failure {ServiceName} reset= 60 actions= restart/5000/restart/5000/restart/5000");

            // Open firewall for inbound connections on the configured port
            Log($"Opening firewall port {port}...");
            RunNetsh($"advfirewall firewall delete rule name=\"QBLockService\"");
            RunNetsh($"advfirewall firewall add rule name=\"QBLockService\" dir=in action=allow protocol=TCP localport={port} description=\"QuickBooks Lock Service — allows workstations to connect\"");

            // Start service
            Log("Starting service...");
            RunSc($"start {ServiceName}");
            System.Threading.Thread.Sleep(2000);

            var finalSvc = GetService();
            if (finalSvc?.Status == ServiceControllerStatus.Running)
            {
                Log($"✓ Service running on port {port}.");
                Log($"  API Key:      {apiKey}");
                Log($"  Health check: http://localhost:{port}/health");
                SetBanner($"Installed successfully — listening on port {port}", "#166534", "#bbf7d0");
                InstallButton.Content = "Update / Reinstall";
                UninstallButton.Visibility = Visibility.Visible;
            }
            else
            {
                Log("⚠ Service may not have started. Check Windows Event Log.");
                SetBanner("Installed but service may not be running — check Event Log.", "#78350f", "#fde68a");
            }
        }
        catch (Exception ex)
        {
            Log($"ERROR: {ex.Message}");
            SetBanner("Installation failed — see log below.", "#7f1d1d", "#fca5a5");
        }
        finally
        {
            InstallButton.IsEnabled = true;
        }
    }

    private void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            "Stop and remove the QuickBooks Lock Service?\n\nThe service files will be left in place.",
            "Uninstall Service", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        UninstallButton.IsEnabled = false;
        LogText.Text = "";
        try
        {
            var svc = GetService();
            if (svc?.Status == ServiceControllerStatus.Running)
            {
                Log("Stopping service...");
                svc.Stop();
                svc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
            }
            Log("Removing service registration...");
            RunSc($"delete {ServiceName}");
            Log("Removing firewall rule...");
            RunNetsh($"advfirewall firewall delete rule name=\"QBLockService\"");
            Log("✓ Service removed.");
            SetBanner("Service uninstalled. Files remain in install directory.", "#1e3a5f", "#93c5fd");
            InstallButton.Content = "Install";
            UninstallButton.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Log($"ERROR: {ex.Message}");
        }
        finally
        {
            UninstallButton.IsEnabled = true;
        }
    }

    private bool ValidateInputs()
    {
        if (string.IsNullOrWhiteSpace(ApiKeyBox.Text) || ApiKeyBox.Text.Length < 16)
        { MessageBox.Show("API Key must be at least 16 characters.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
        if (!int.TryParse(PortBox.Text, out var p) || p < 1024 || p > 65535)
        { MessageBox.Show("Port must be a number between 1024 and 65535.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
        if (string.IsNullOrWhiteSpace(InstallDirBox.Text))
        { MessageBox.Show("Install directory is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
        if (!int.TryParse(TimeoutBox.Text, out var t) || t < 1)
        { MessageBox.Show("Timeout must be a positive number of minutes.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
        return true;
    }

    private static ServiceController? GetService()
    {
        try { return ServiceController.GetServices().FirstOrDefault(s => s.ServiceName == ServiceName); }
        catch { return null; }
    }

    private static void RunSc(string args) => RunExe("sc.exe", args);

    private static void RunNetsh(string args) => RunExe("netsh.exe", args);

    private static void RunExe(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit(10_000);
    }

    private void Log(string message)
    {
        Dispatcher.Invoke(() =>
        {
            LogText.Text += message + "\n";
            LogScroll.ScrollToEnd();
        });
    }
}
