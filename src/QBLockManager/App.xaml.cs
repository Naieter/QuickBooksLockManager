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

    protected override void OnStartup(StartupEventArgs e)
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

        if (NeedsSetup(settings))
        {
            var wizard = new SetupWizardWindow();
            bool? result = wizard.ShowDialog();
            if (result != true || wizard.ConfiguredSettings == null)
                return;

            settings = wizard.ConfiguredSettings;
        }

        var mainWindow = new MainWindow(settings);
        mainWindow.Show();
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
}
