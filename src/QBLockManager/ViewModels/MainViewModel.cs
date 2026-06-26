using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QBLockManager.Models;
using QBLockManager.Services;

namespace QBLockManager.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppSettings _settings;
    private readonly LockServiceClient _lockClient;
    private readonly FileScanner _scanner;
    private readonly HeartbeatManager _heartbeat;
    private readonly string _appInstanceId;
    private readonly System.Timers.Timer _refreshTimer;
    private readonly System.Timers.Timer _qbMonitorTimer;
    private bool _disposed;

    [ObservableProperty] private string _statusMessage = "Initializing...";
    [ObservableProperty] private string _serviceStatus = "Connecting...";
    [ObservableProperty] private bool _serviceReachable;
    [ObservableProperty] private string _filterText = string.Empty;
    [ObservableProperty] private CompanyFileViewModel? _selectedFile;
    [ObservableProperty] private ActiveLockViewModel? _selectedActiveLock;
    [ObservableProperty] private bool _isAdminMode;
    [ObservableProperty] private bool _isBusy;
    public bool IsShuttingDown { get; private set; }

    public ObservableCollection<CompanyFileViewModel> Files { get; } = new();
    public ObservableCollection<ActiveLockViewModel> MyActiveLocks { get; } = new();

    public bool MultiFileMode => _settings.MultiFileMode;

    public MainViewModel(AppSettings settings)
    {
        _settings = settings;
        _appInstanceId = Guid.NewGuid().ToString("N")[..16];
        _isAdminMode = settings.IsAdminMode;

        _lockClient = new LockServiceClient(
            settings.LockServiceBaseUrl,
            settings.ApiKey,
            settings.AdminApiKey,
            settings.ServiceTimeoutSeconds);

        _scanner = new FileScanner();

        _heartbeat = new HeartbeatManager(_lockClient, _appInstanceId, settings.HeartbeatIntervalSeconds);
        _heartbeat.LockLost += OnLockLost;
        _heartbeat.CloseRequested += () => _ = OnForceCloseRequestedAsync();
        _heartbeat.OpenFileRequested += fileKey => _ = OnRemoteOpenFileRequestedAsync(fileKey);

        _refreshTimer = new System.Timers.Timer(30_000);
        _refreshTimer.Elapsed += async (_, _) => await RefreshAsync();
        _refreshTimer.AutoReset = true;
        _refreshTimer.Start();

        // Poll every 5 s: if QB has closed but locks are still active, release them.
        _qbMonitorTimer = new System.Timers.Timer(5_000);
        _qbMonitorTimer.Elapsed += async (_, _) => await CheckQuickBooksAsync();
        _qbMonitorTimer.AutoReset = true;
        _qbMonitorTimer.Start();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            ServiceReachable = await _lockClient.IsReachableAsync();
            ServiceStatus = ServiceReachable
                ? $"Connected to {_settings.LockServiceBaseUrl}"
                : "OFFLINE — file opening is blocked";

            if (!ServiceReachable)
            {
                StatusMessage = "The lock service is unavailable. To prevent conflicts, QuickBooks files cannot be opened through this app until the lock service is reachable. Reach out to Nathan";
                return;
            }

            // Scan local files
            var scanned = _scanner.Scan(_settings);

            // Overlay with lock info
            var allLocks = await _lockClient.GetAllLocksAsync();
            var lockByKey = allLocks.ToDictionary(l => l.FileKey, StringComparer.OrdinalIgnoreCase);

            Application.Current.Dispatcher.Invoke(() =>
            {
                Files.Clear();
                foreach (var entry in scanned)
                {
                    var vm = new CompanyFileViewModel(entry);

                    if (lockByKey.TryGetValue(entry.FileKey, out var lockInfo))
                    {
                        vm.CurrentLock = lockInfo;
                        if (lockInfo.AppInstanceId == _appInstanceId)
                            vm.Availability = FileAvailability.LockedByMe;
                        else if (lockInfo.IsStale)
                            vm.Availability = FileAvailability.Stale;
                        else
                            vm.Availability = FileAvailability.LockedByOther;
                    }
                    else
                    {
                        vm.Availability = FileAvailability.Available;
                        vm.CurrentLock = null;
                    }

                    Files.Add(vm);
                }

                // Also add locked files not found locally (on other machines)
                foreach (var lockInfo in allLocks)
                {
                    if (!Files.Any(f => f.FileKey.Equals(lockInfo.FileKey, StringComparison.OrdinalIgnoreCase)))
                    {
                        var vm = new CompanyFileViewModel(new CompanyFileEntry
                        {
                            FileKey = lockInfo.FileKey,
                            FileName = lockInfo.FileName,
                            LocalPath = lockInfo.LocalPath ?? "(not available on this workstation)",
                            ExistsLocally = false
                        });
                        vm.Availability = lockInfo.AppInstanceId == _appInstanceId
                            ? FileAvailability.LockedByMe
                            : (lockInfo.IsStale ? FileAvailability.Stale : FileAvailability.LockedByOther);
                        vm.CurrentLock = lockInfo;
                        Files.Add(vm);
                    }
                }

                // Refresh my active locks list
                var prevLockId = SelectedActiveLock?.LockId;
                MyActiveLocks.Clear();
                foreach (var kvp in _heartbeat.ActiveLocks)
                {
                    var lockDto = allLocks.FirstOrDefault(l => l.LockId == kvp.Key);
                    MyActiveLocks.Add(new ActiveLockViewModel
                    {
                        LockId = kvp.Key,
                        FileKey = kvp.Value.FileKey,
                        FileName = kvp.Value.FileName,
                        LocalPath = kvp.Value.LocalPath,
                        AcquiredAt = kvp.Value.AcquiredAt,
                        LastHeartbeat = lockDto?.LastHeartbeatUtc ?? DateTime.UtcNow
                    });
                }

                // Keep the previously selected lock selected; fall back to the first one.
                SelectedActiveLock = MyActiveLocks.FirstOrDefault(l => l.LockId == prevLockId)
                                  ?? MyActiveLocks.FirstOrDefault();
            });

            StatusMessage = $"Loaded {Files.Count} file(s). {MyActiveLocks.Count} lock(s) active.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error refreshing: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CheckQuickBooksAsync()
    {
        if (_heartbeat.ActiveLocks.Count == 0) return;
        if (QuickBooksLauncher.IsQuickBooksRunning()) return;

        // QB has exited while we still hold locks — release them all.
        QuickBooksAutoLogin.ResetAttempts();
        await _heartbeat.ReleaseAllAsync();

        Application.Current.Dispatcher.Invoke(() =>
            StatusMessage = "QuickBooks was closed — locks released automatically.");

        await RefreshAsync();
    }

    [RelayCommand]
    public async Task OpenFileAsync()
    {
        if (SelectedFile == null) return;
        await OpenFileInternalAsync(SelectedFile, switchMode: false);
    }

    [RelayCommand]
    public async Task SwitchFileAsync()
    {
        if (SelectedFile == null) return;
        await OpenFileInternalAsync(SelectedFile, switchMode: true);
    }

    [RelayCommand]
    public async Task OpenAnotherAsync()
    {
        if (SelectedFile == null) return;
        if (!_settings.MultiFileMode)
        {
            MessageBox.Show("Multi-file mode is not enabled in settings.", "Not Available",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await OpenFileInternalAsync(SelectedFile, switchMode: false);
    }

    private async Task OpenFileInternalAsync(CompanyFileViewModel fileVm, bool switchMode)
    {
        if (!ServiceReachable)
        {
            MessageBox.Show(
                "The lock service is unavailable. To prevent conflicts, QuickBooks files cannot be opened through this app until the lock service is reachable. Reach out to Nathan",
                "Lock Service Unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!fileVm.ExistsLocally)
        {
            MessageBox.Show(
                $"This file is not currently available on this workstation:\n{fileVm.LocalPath}",
                "File Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.QuickBooksExePath) ||
            !System.IO.File.Exists(_settings.QuickBooksExePath))
        {
            MessageBox.Show(
                "QuickBooks could not be found. Please configure the QuickBooks executable path in Settings.",
                "QuickBooks Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (fileVm.Availability == FileAvailability.LockedByMe)
        {
            MessageBox.Show("You currently have this file locked.", "Already Locked",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        bool qbRunning = QuickBooksLauncher.IsQuickBooksRunning();

        if (switchMode && (_heartbeat.ActiveLocks.Count > 0 || qbRunning))
        {
            // Switch mode: confirm, release existing locks, close QB, then continue.
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Switch to {fileVm.FileName}?");
            sb.AppendLine();
            if (_heartbeat.ActiveLocks.Count > 0)
                sb.AppendLine($"• {_heartbeat.ActiveLocks.Count} active lock(s) will be released.");
            if (qbRunning)
                sb.AppendLine("• QuickBooks will be closed and reopened with the new file.");
            sb.AppendLine();
            sb.Append("Continue?");

            if (MessageBox.Show(sb.ToString(), "Switch File", MessageBoxButton.YesNo, MessageBoxImage.Question)
                != MessageBoxResult.Yes) return;

            IsBusy = true;
            StatusMessage = "Releasing locks and closing QuickBooks...";
            try
            {
                await _heartbeat.ReleaseAllAsync();
                if (qbRunning) await QuickBooksLauncher.CloseQuickBooksAsync();
            }
            finally { IsBusy = false; }
        }
        else if (!switchMode && qbRunning)
        {
            var r = MessageBox.Show(
                "QuickBooks is already running. Opening a company file through the Lock Manager will attempt to load it in the existing QuickBooks window.\n\nIMPORTANT: Do NOT open additional company files from inside QuickBooks — only use the Lock Manager.\n\nContinue?",
                "QuickBooks Already Running", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;
        }

        IsBusy = true;
        StatusMessage = $"Acquiring lock for {fileVm.FileName}...";

        try
        {
            var result = await _lockClient.AcquireAsync(
                fileVm.FileKey, fileVm.FileName, fileVm.LocalPath,
                _settings.UserName, _settings.DisplayName, _settings.Email,
                Environment.MachineName, _appInstanceId);

            if (result.Status == "Denied")
            {
                var holder = result.CurrentHolder;
                MessageBox.Show(
                    $"This QuickBooks file is currently in use by {holder?.UserName ?? "someone"} on {holder?.MachineName ?? "another computer"} since {holder?.AcquiredAtUtc:g}.",
                    "File In Use", MessageBoxButton.OK, MessageBoxImage.Warning);
                StatusMessage = $"Lock denied for {fileVm.FileName}.";
                return;
            }

            if (result.Status is not ("Acquired" or "AlreadyOwned") || result.Lock == null)
            {
                MessageBox.Show($"Could not acquire lock: {result.Message}", "Lock Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Register heartbeat BEFORE launching QB
            _heartbeat.RegisterLock(new ActiveFileLock
            {
                LockId = result.Lock.LockId,
                FileKey = fileVm.FileKey,
                FileName = fileVm.FileName,
                LocalPath = fileVm.LocalPath,
                AcquiredAt = result.Lock.AcquiredAtUtc
            });

            var launch = QuickBooksLauncher.Launch(_settings.QuickBooksExePath, fileVm.LocalPath);
            if (launch.Success)
                QuickBooksAutoLogin.BeginAutoLogin(_settings.QuickBooksPassword);

            if (!launch.Success)
            {
                // Launch failed — release the lock we just acquired
                await _lockClient.ReleaseAsync(result.Lock.LockId, _appInstanceId);
                _heartbeat.UnregisterLock(result.Lock.LockId);
                MessageBox.Show(launch.Message, "Launch Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            StatusMessage = $"Opened {fileVm.FileName}. Lock active.";
            if (launch.ReusedExistingProcess)
            {
                MessageBox.Show(launch.Message, "QuickBooks Running", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            MessageBox.Show($"An error occurred: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync();
        }
    }

    [RelayCommand]
    public async Task ReleaseSelectedLockAsync()
    {
        if (SelectedActiveLock == null) return;

        var confirm = MessageBox.Show(
            $"Release lock on {SelectedActiveLock.FileName}?\n\nMake sure you have already closed this file in QuickBooks before releasing the lock.",
            "Release Lock", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        IsBusy = true;
        try
        {
            var ok = await _lockClient.ReleaseAsync(SelectedActiveLock.LockId, _appInstanceId);
            _heartbeat.UnregisterLock(SelectedActiveLock.LockId);
            StatusMessage = ok ? $"Lock released for {SelectedActiveLock.FileName}." : "Release failed.";
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync();
        }
    }

    [RelayCommand]
    public async Task AdminForceUnlockAsync()
    {
        if (SelectedFile?.CurrentLock == null) return;

        if (string.IsNullOrWhiteSpace(_settings.AdminApiKey))
        {
            MessageBox.Show("Admin API key is not configured.", "Admin Required",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var holder = SelectedFile.CurrentLock;
        var warn = MessageBox.Show(
            $"WARNING: Force-unlocking this file does NOT close it in QuickBooks.\n\n{holder.UserName} on {holder.MachineName} may still have the file open. If they save, it will overwrite any changes made after this force unlock.\n\nVerify with {holder.UserName} that they are done before proceeding.\n\nContinue with force unlock?",
            "Force Unlock — Admin Action", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (warn != MessageBoxResult.Yes) return;

        var (ok, msg) = await _lockClient.ForceReleaseAsync(
            holder.LockId, _settings.UserName,
            $"Admin force unlock by {_settings.UserName} on {Environment.MachineName}",
            _appInstanceId);

        MessageBox.Show(msg, ok ? "Force Unlock Completed" : "Force Unlock Failed",
            MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Error);

        await RefreshAsync();
    }

    [RelayCommand]
    public async Task ShowAuditLogAsync()
    {
        try
        {
            var entries = await _lockClient.GetAuditLogAsync(200, SelectedFile?.FileKey);
            var window = new Views.AuditLogWindow(entries);
            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not load audit log: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    public void OpenSettings()
    {
        var window = new Views.SettingsWindow(_settings);
        if (window.ShowDialog() == true)
        {
            new SettingsService().Save(_settings);
            _ = RefreshAsync();
        }
    }

    private async Task OnForceCloseRequestedAsync()
    {
        QuickBooksAutoLogin.ResetAttempts();
        await _heartbeat.ReleaseAllAsync();
        if (QuickBooksLauncher.IsQuickBooksRunning())
            await QuickBooksLauncher.CloseQuickBooksAsync();
        Application.Current.Dispatcher.Invoke(() =>
            StatusMessage = "QuickBooks was closed by an administrator.");
        await RefreshAsync();
    }

    private async Task OnRemoteOpenFileRequestedAsync(string fileKey)
    {
        // Refresh so we have the latest availability — the force-released lock should be gone.
        await RefreshAsync();

        CompanyFileViewModel? fileVm = null;
        Application.Current.Dispatcher.Invoke(() =>
            fileVm = Files.FirstOrDefault(f =>
                f.FileKey.Equals(fileKey, StringComparison.OrdinalIgnoreCase)));

        if (fileVm == null || !fileVm.ExistsLocally)
        {
            Application.Current.Dispatcher.Invoke(() =>
                StatusMessage = "The force-unlocked file is not available on this workstation.");
            return;
        }

        Application.Current.Dispatcher.Invoke(() =>
        {
            IsBusy = true;
            StatusMessage = $"Opening {fileVm.FileName}...";
        });

        try
        {
            var result = await _lockClient.AcquireAsync(
                fileVm.FileKey, fileVm.FileName, fileVm.LocalPath,
                _settings.UserName, _settings.DisplayName, _settings.Email,
                Environment.MachineName, _appInstanceId);

            if (result.Status is not ("Acquired" or "AlreadyOwned") || result.Lock == null)
            {
                Application.Current.Dispatcher.Invoke(() =>
                    StatusMessage = $"Could not open {fileVm.FileName}: {result.Message}");
                return;
            }

            _heartbeat.RegisterLock(new ActiveFileLock
            {
                LockId = result.Lock.LockId,
                FileKey = fileVm.FileKey,
                FileName = fileVm.FileName,
                LocalPath = fileVm.LocalPath,
                AcquiredAt = result.Lock.AcquiredAtUtc
            });

            var launch = QuickBooksLauncher.Launch(_settings.QuickBooksExePath, fileVm.LocalPath);
            if (launch.Success)
                QuickBooksAutoLogin.BeginAutoLogin(_settings.QuickBooksPassword);

            if (!launch.Success)
            {
                await _lockClient.ReleaseAsync(result.Lock.LockId, _appInstanceId);
                _heartbeat.UnregisterLock(result.Lock.LockId);
                Application.Current.Dispatcher.Invoke(() =>
                    StatusMessage = $"Lock acquired but QuickBooks failed to launch: {launch.Message}");
                return;
            }

            Application.Current.Dispatcher.Invoke(() =>
                StatusMessage = $"Opened {fileVm.FileName}.");
        }
        catch (Exception ex)
        {
            Application.Current.Dispatcher.Invoke(() =>
                StatusMessage = $"Auto-open failed: {ex.Message}");
        }
        finally
        {
            Application.Current.Dispatcher.Invoke(() => IsBusy = false);
            await RefreshAsync();
        }
    }

    private void OnLockLost(string lockId)
    {
        _heartbeat.UnregisterLock(lockId);
        Application.Current.Dispatcher.Invoke(() =>
        {
            StatusMessage = $"WARNING: A lock heartbeat failed and the lock may have expired. Please verify your file status and re-acquire if needed.";
        });
    }

    public async Task<bool> OnWindowClosingAsync()
    {
        if (_heartbeat.ActiveLocks.Count > 0)
        {
            var result = MessageBox.Show(
                $"You have {_heartbeat.ActiveLocks.Count} active file lock(s).\n\nMake sure you have closed those files in QuickBooks before releasing locks.\n\nRelease all locks now?",
                "Active Locks", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

            if (result == MessageBoxResult.Cancel) return false;
            if (result == MessageBoxResult.Yes)
                await _heartbeat.ReleaseAllAsync();
        }
        new SettingsService().Save(_settings);
        IsShuttingDown = true;
        _refreshTimer.Stop();
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refreshTimer.Dispose();
        _qbMonitorTimer.Dispose();
        _heartbeat.Dispose();
    }
}

public partial class CompanyFileViewModel : ObservableObject
{
    public string FileKey { get; }
    public string FileName { get; }
    public string LocalPath { get; }
    public bool ExistsLocally { get; }

    [ObservableProperty] private FileAvailability _availability;
    [ObservableProperty] private LockInfoDto? _currentLock;

    public string StatusText => Availability switch
    {
        FileAvailability.Available => "Available",
        FileAvailability.LockedByMe => "Locked by me",
        FileAvailability.LockedByOther => $"In use by {CurrentLock?.UserName}",
        FileAvailability.Stale => $"Stale lock ({CurrentLock?.UserName})",
        FileAvailability.NotFound => "Not found locally",
        _ => "Unknown"
    };

    public string UserText => CurrentLock?.UserName ?? "";
    public string ComputerText => CurrentLock?.MachineName ?? "";
    public string LockedSinceText => CurrentLock != null ? CurrentLock.AcquiredAtUtc.ToLocalTime().ToString("g") : "";
    public string LastSeenText => CurrentLock != null ? CurrentLock.LastHeartbeatUtc.ToLocalTime().ToString("g") : "";

    public CompanyFileViewModel(CompanyFileEntry entry)
    {
        FileKey = entry.FileKey;
        FileName = entry.FileName;
        LocalPath = entry.LocalPath;
        ExistsLocally = entry.ExistsLocally;
        Availability = entry.Availability;
    }
}

public class ActiveLockViewModel
{
    public string LockId { get; set; } = string.Empty;
    public string FileKey { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string LocalPath { get; set; } = string.Empty;
    public DateTime AcquiredAt { get; set; }
    public DateTime LastHeartbeat { get; set; }
    public string AcquiredAtText => AcquiredAt.ToLocalTime().ToString("g");
    public string LastHeartbeatText => LastHeartbeat.ToLocalTime().ToString("g");
}
