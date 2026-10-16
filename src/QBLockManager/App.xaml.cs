using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using QBLockManager.Models;
using QBLockManager.Services;
using QBLockManager.Views;

namespace QBLockManager;

public partial class App : Application
{
    private static readonly string InstallDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "QBLockManager");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                $"An unexpected error occurred:\n\n{args.Exception.Message}\n\n{args.Exception.GetType().Name}",
                "QuickBooks Lock Manager — Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        // Self-install when run from outside the install directory.
        if (SelfInstallIfNeeded())
            return;

        var settingsSvc = new SettingsService();
        var settings = settingsSvc.Load();

        // Check for updates — only when running from the installed location.
        var currentExe   = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
        var installedExe = Path.Combine(InstallDir, "QBLockManager.exe");
        if (currentExe.Equals(installedExe, StringComparison.OrdinalIgnoreCase))
        {
            if (await TryApplyUpdateAsync(settings, installedExe))
            {
                Current.Shutdown();
                return;
            }
        }

        if (NeedsSetup(settings))
        {
            var profile = SettingsService.LoadConnectionProfile() ?? FindDroppedProfile().profile;
            var firstRun = new FirstRunWindow(profile);
            bool? result = firstRun.ShowDialog();
            if (result != true || firstRun.ConfiguredSettings == null)
                return;

            settings = firstRun.ConfiguredSettings;
        }
        else
        {
            // Already set up — check well-known drop locations for a credential update from admin
            var (profile, profilePath) = FindDroppedProfile();
            if (profile != null &&
                (profile.ServerUrl != settings.LockServiceBaseUrl || profile.ApiKey != settings.ApiKey))
            {
                var r = MessageBox.Show(
                    "Your administrator has sent updated connection settings.\n\nApply them now?",
                    "Updated Server Settings",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r == MessageBoxResult.Yes)
                {
                    settings.LockServiceBaseUrl = profile.ServerUrl;
                    settings.ApiKey = profile.ApiKey;
                    settingsSvc.Save(settings);
                }
                // Delete either way — a declined or already-applied file must never
                // resurface and re-prompt (or silently re-apply stale values) on a later launch.
                try { File.Delete(profilePath!); } catch { }
            }
        }

        var mainWindow = new MainWindow(settings);
        mainWindow.Show();
    }

    // Returns true if an update was downloaded and the updater process was launched.
    // The caller must call Application.Current.Shutdown() when this returns true.
    private static async Task<bool> TryApplyUpdateAsync(AppSettings settings, string installedExe)
    {
        var update = await Services.UpdateChecker.CheckAsync(settings.GitHubToken);
        if (update == null) return false;

        var sizeMb = update.SizeBytes / 1024 / 1024;
        var prompt = MessageBox.Show(
            $"QuickBooks Lock Manager {update.TagName} is available.\n\nInstall update now? (~{sizeMb} MB)",
            "Update Available",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (prompt != MessageBoxResult.Yes) return false;

        var tempFile      = Path.Combine(Path.GetTempPath(), "QBLockManager_update.exe");
        var progressWindow = new Views.UpdateProgressWindow(update.TagName);
        progressWindow.Show();

        try
        {
            var progress = new Progress<double>(v => progressWindow.SetProgress(v));
            await Services.UpdateChecker.DownloadAsync(
                update, tempFile, settings.GitHubToken, progress);

            progressWindow.ForceClose();
            Services.UpdateChecker.LaunchUpdater(installedExe, tempFile);
            return true;
        }
        catch (Exception ex)
        {
            progressWindow.ForceClose();
            try { File.Delete(tempFile); } catch { }
            MessageBox.Show(
                $"Update download failed:\n\n{ex.Message}\n\nLaunching the current version.",
                "Update Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }
    }

    // Returns true if installation was triggered and this process should exit.
    private static bool SelfInstallIfNeeded()
    {
        var currentExe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
        var installedExe = Path.Combine(InstallDir, "QBLockManager.exe");

        if (currentExe.Equals(installedExe, StringComparison.OrdinalIgnoreCase))
            return false; // Running from the install location — normal launch.

        // Already installed and the file hasn't changed — don't nag the user.
        if (File.Exists(installedExe) && ExesAreIdentical(currentExe, installedExe))
            return false;

        bool isUpdate = File.Exists(installedExe);
        string action = isUpdate ? "Update" : "Install";
        string message = isUpdate
            ? $"Update QuickBooks Lock Manager?\n\nNew version will replace:\n{installedExe}"
            : $"Install QuickBooks Lock Manager?\n\nWill be installed to:\n{installedExe}";

        var result = MessageBox.Show(message, $"{action} QuickBooks Lock Manager",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            // Let them run in-place without installing.
            return false;
        }

        try
        {
            Directory.CreateDirectory(InstallDir);
            File.Copy(currentExe, installedExe, overwrite: true);

            // Carry the connection profile alongside the exe so the wizard can pre-fill it
            var profileSrc = Path.Combine(Path.GetDirectoryName(currentExe)!, "qblm-connect.json");
            if (File.Exists(profileSrc))
                try { File.Copy(profileSrc, Path.Combine(InstallDir, "qblm-connect.json"), overwrite: true); } catch { }

            CreateShortcut(installedExe, InstallDir,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    "QuickBooks Lock Manager.lnk"));

            CreateShortcut(installedExe, InstallDir,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Microsoft", "Windows", "Start Menu", "Programs",
                    "QuickBooks Lock Manager.lnk"));

            Process.Start(new ProcessStartInfo(installedExe) { UseShellExecute = true });
            Current.Shutdown();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Installation failed:\n\n{ex.Message}\n\nYou can still run the app from its current location.",
                "Install Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false; // Fall through to normal run even if install failed.
        }

        return true;
    }

    private static bool ExesAreIdentical(string a, string b)
    {
        try
        {
            var ia = new FileInfo(a);
            var ib = new FileInfo(b);
            return ia.Length == ib.Length && ia.LastWriteTimeUtc == ib.LastWriteTimeUtc;
        }
        catch { return false; }
    }

    private static void CreateShortcut(string targetExe, string workingDir, string shortcutPath)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic sc = shell.CreateShortcut(shortcutPath);
            sc.TargetPath = targetExe;
            sc.WorkingDirectory = workingDir;
            sc.Description = "QuickBooks Lock Manager — Always open QB files through this app";
            sc.Save();
        }
        catch { }
    }

    private static bool NeedsSetup(AppSettings s) =>
        !s.SetupCompleted ||
        string.IsNullOrWhiteSpace(s.LockServiceBaseUrl) ||
        s.WatchedFolders.Count == 0 ||
        s.WatchedFolders.All(f => string.IsNullOrWhiteSpace(f.Path));

    // Looks for qblm-connect.json in well-known drop locations (Desktop, Downloads, next to exe).
    private static (ConnectionProfile? profile, string? path) FindDroppedProfile()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "qblm-connect.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "qblm-connect.json"),
            Path.Combine(AppContext.BaseDirectory, "qblm-connect.json"),
        };

        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            try
            {
                var json = File.ReadAllText(path);
                var p = System.Text.Json.JsonSerializer.Deserialize<ConnectionProfile>(json,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (p != null && !string.IsNullOrWhiteSpace(p.ServerUrl))
                    return (p, path);
            }
            catch { }
        }
        return (null, null);
    }
}
