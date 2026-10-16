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
    private readonly SharedFileLockStore _fileStore;
    private readonly HeartbeatManager _heartbeat;
    private readonly string _appInstanceId;
    private readonly System.Timers.Timer _refreshTimer;
    private readonly System.Timers.Timer _qbMonitorTimer;
    private bool _disposed;

    [ObservableProperty] private string _statusMessage = "Initializing...";
    [ObservableProperty] private string _serviceStatus = "Connecting...";
    [ObservableProperty] private bool _serviceReachable;
    // True when a lock registry is available: the server, or the shared-folder backup file.
    [ObservableProperty] private bool _canOpenFiles;
    [ObservableProperty] private string _filterText = string.Empty;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveStaleLockCommand))]
    private CompanyFileViewModel? _selectedFile;
    [ObservableProperty] private ActiveLockViewModel? _selectedActiveLock;
    [ObservableProperty] private bool _isBusy;
    public bool IsShuttingDown { get; private set; }

    public ObservableCollection<CompanyFileViewModel> Files { get; } = new();
    public ObservableCollection<ActiveLockViewModel> MyActiveLocks { get; } = new();

    public bool MultiFileMode => _settings.MultiFileMode;

    public string AppVersion
    {
        get
        {
            var v = Services.UpdateChecker.CurrentVersion;
            return v.Build > 0 ? $"v{v.Major}.{v.Minor}.{v.Build}" : $"v{v.Major}.{v.Minor}";
        }
    }

    public MainViewModel(AppSettings settings)
    {
        _settings = settings;
        _appInstanceId = Guid.NewGuid().ToString("N")[..16];
        _lockClient = new LockServiceClient(
            settings.LockServiceBaseUrl,
            settings.ApiKey,
            settings.ServiceTimeoutSeconds);

        _scanner = new FileScanner();

        // Backup lock registry lives in the shared QB folder; resolved per call since the
        // Google Drive letter can change while the app is running.
        _fileStore = new SharedFileLockStore(() =>
        {
            var folder = _settings.WatchedFolders
                .Select(f => f.Path)
                .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
            return folder == null ? null : DriveLetterResolver.Resolve(folder);
        });

        _heartbeat = new HeartbeatManager(_lockClient, _fileStore, _appInstanceId, settings.HeartbeatIntervalSeconds);
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

            // Server down (or rejecting our key): the admin may have published new settings.
            if (!ServiceReachable && await TryAdoptSharedConnectionAsync())
                ServiceReachable = await _lockClient.IsReachableAsync();

            var backupAvailable = await Task.Run(() => _fileStore.IsAvailable);
            CanOpenFiles = ServiceReachable || backupAvailable;

            ServiceStatus = ServiceReachable
                ? $"Connected to {_settings.LockServiceBaseUrl}"
                : backupAvailable
                    ? $"OFFLINE — using shared-folder backup ({SharedFileLockStore.FileName})"
                    : "OFFLINE — shared folder unavailable, file opening is blocked";

            if (!ServiceReachable && !backupAvailable)
            {
                StatusMessage = "The lock service is unavailable and the shared QuickBooks folder can't be reached, so there is no way to check who has a file open. File opening is blocked until one of them is back.";
                return;
            }

            // Scan local files off the UI thread — Google Drive / network mounts can
            // block Directory calls for many seconds, which froze the window at startup.
            var scanned = await Task.Run(() => _scanner.Scan(_settings));

            // Overlay with lock info: server is authoritative when reachable; the shared-folder
            // file adds anything the server doesn't know (locks taken while it was down).
            var fileLocks = await _fileStore.ReadAsync();
            List<LockInfoDto> allLocks;
            if (ServiceReachable)
            {
                allLocks = await _lockClient.GetAllLocksAsync();
                foreach (var fl in fileLocks)
                {
                    bool known = allLocks.Any(l => l.LockId == fl.LockId ||
                                                   l.FileKey.Equals(fl.FileKey, StringComparison.OrdinalIgnoreCase));
                    if (!known) allLocks.Add(fl);
                }
            }
            else
            {
                allLocks = fileLocks;
            }
            // One lock per file for display: a fresh lock beats a stale leftover for the same file.
            var lockByKey = new Dictionary<string, LockInfoDto>(StringComparer.OrdinalIgnoreCase);
            foreach (var l in allLocks.OrderBy(l => l.IsStale)) lockByKey.TryAdd(l.FileKey, l);

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

            StatusMessage = $"Loaded {Files.Count} file(s). {MyActiveLocks.Count} lock(s) active."
                          + (_connectionNotice == null ? "" : " " + _connectionNotice);
            _connectionNotice = null;
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

    private string? _connectionNotice;
    private string? _failedProfileSignature;   // "url|key" of a published file whose server didn't answer
    private DateTime _failedProfileAtUtc;

    // The admin can publish new server settings in <shared QB folder>\QBLockCert\qblm-connect.json.
    // Adopted only if that server answers with those credentials, so a typo in the file can't
    // strand every workstation on a dead address. Returns true if the settings were switched.
    private async Task<bool> TryAdoptSharedConnectionAsync()
    {
        var folders = _settings.WatchedFolders.Select(f => f.Path)
            .Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (folders.Count == 0) return false;

        var profile = await Task.Run(() => SharedConnectionFile.Find(folders));
        if (profile == null) return false;

        var url = LockServiceClient.NormalizeUrl(profile.ServerUrl).TrimEnd('/');
        var key = (profile.ApiKey ?? "").Trim();
        var current = LockServiceClient.NormalizeUrl(_settings.LockServiceBaseUrl).TrimEnd('/');
        if (url.Equals(current, StringComparison.OrdinalIgnoreCase) && key == _settings.ApiKey)
            return false; // same settings we already have — the server is simply down

        // Don't hammer a published address that isn't answering; look again in two minutes.
        var signature = url + "|" + key;
        if (signature == _failedProfileSignature &&
            DateTime.UtcNow - _failedProfileAtUtc < TimeSpan.FromMinutes(2))
            return false;

        if (!await LockServiceClient.TestConnectionAsync(url, key))
        {
            _failedProfileSignature = signature;
            _failedProfileAtUtc = DateTime.UtcNow;
            return false;
        }

        _settings.LockServiceBaseUrl = url;
        _settings.ApiKey = key;
        new SettingsService().Save(_settings);
        ApplySettingsLive();
        _connectionNotice = $"Switched to the new lock server ({url}) from the shared connection file.";
        return true;
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
        if (!ServiceReachable && !_fileStore.IsAvailable)
        {
            MessageBox.Show(
                "The lock service is unavailable and the shared QuickBooks folder can't be reached, so there is no way to check who has this file open.\n\nFile opening is blocked until one of them is back.",
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
            // The shared-folder file may hold a lock the server never saw (taken while it was
            // down), so check it in both modes. Stale entries (no heartbeat for 5 min) are
            // overridable, same as the server's own rule.
            var fileLocks = await _fileStore.ReadAsync();
            var foreign = fileLocks.FirstOrDefault(l =>
                l.FileKey.Equals(fileVm.FileKey, StringComparison.OrdinalIgnoreCase) &&
                l.AppInstanceId != _appInstanceId && !l.IsStale);
            if (foreign != null)
            {
                MessageBox.Show(
                    $"This QuickBooks file is currently in use by {foreign.UserName} on {foreign.MachineName} since {foreign.AcquiredAtUtc.ToLocalTime():g}.",
                    "File In Use", MessageBoxButton.OK, MessageBoxImage.Warning);
                StatusMessage = $"Lock denied for {fileVm.FileName}.";
                return;
            }

            LockInfoDto lockInfo;
            bool offlineOnly = !ServiceReachable;

            if (ServiceReachable)
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

                lockInfo = result.Lock;
            }
            else
            {
                // Server down: the shared-folder file is the lock registry.
                lockInfo = new LockInfoDto
                {
                    LockId = Guid.NewGuid().ToString("N"),
                    FileKey = fileVm.FileKey,
                    FileName = fileVm.FileName,
                    UserName = _settings.UserName,
                    DisplayName = _settings.DisplayName,
                    MachineName = Environment.MachineName,
                    LocalPath = fileVm.LocalPath,
                    AppInstanceId = _appInstanceId,
                    AcquiredAtUtc = DateTime.UtcNow,
                    LastHeartbeatUtc = DateTime.UtcNow,
                    Status = "Offline"
                };
            }

            // Mirror into the shared folder in both modes, so a later server outage
            // doesn't hide this lock from other machines.
            lockInfo.AppInstanceId = _appInstanceId;
            lockInfo.LastHeartbeatUtc = DateTime.UtcNow;
            await _fileStore.UpsertAsync(lockInfo);

            // Register heartbeat BEFORE launching QB
            _heartbeat.RegisterLock(new ActiveFileLock
            {
                LockId = lockInfo.LockId,
                FileKey = fileVm.FileKey,
                FileName = fileVm.FileName,
                LocalPath = fileVm.LocalPath,
                AcquiredAt = lockInfo.AcquiredAtUtc,
                OfflineOnly = offlineOnly
            });

            var launch = QuickBooksLauncher.Launch(_settings.QuickBooksExePath, fileVm.LocalPath);
            if (launch.Success)
                QuickBooksAutoLogin.BeginAutoLogin(_settings.QuickBooksPassword);

            if (!launch.Success)
            {
                // Launch failed — release the lock we just acquired
                await _heartbeat.ReleaseLockAsync(lockInfo.LockId);
                MessageBox.Show(launch.Message, "Launch Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            StatusMessage = offlineOnly
                ? $"Opened {fileVm.FileName}. Lock recorded in shared folder (server offline)."
                : $"Opened {fileVm.FileName}. Lock active.";
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
            var ok = await _heartbeat.ReleaseLockAsync(SelectedActiveLock.LockId);
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

        var holder = SelectedFile.CurrentLock;

        // A lock the server doesn't know about (taken while it was down, or we're offline now)
        // can only be cleared from the shared-folder file — we can't close QB remotely.
        if (!ServiceReachable || holder.Status == "Offline")
        {
            var warnOffline = MessageBox.Show(
                $"The lock server is not available for this lock. This will remove {holder.UserName}'s lock from the shared folder, but it CANNOT close QuickBooks on {holder.MachineName}.\n\nMake sure they have actually closed the file before continuing.\n\nRemove the lock?",
                "Force Kick User (offline)", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (warnOffline != MessageBoxResult.Yes) return;

            await _fileStore.RemoveAsync(holder.LockId);
            MessageBox.Show("Backup lock removed from the shared folder.", "Force Kick Completed",
                MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshAsync();
            return;
        }

        var warn = MessageBox.Show(
            $"Do you want to kick this user to unlock the file?",
            "Force Kick User", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (warn != MessageBoxResult.Yes) return;

        var (ok, msg) = await _lockClient.ForceReleaseAsync(
            holder.LockId, _settings.UserName,
            $"Admin force unlock by {_settings.UserName} on {Environment.MachineName}",
            _appInstanceId);

        if (ok) await _fileStore.RemoveAsync(holder.LockId);

        MessageBox.Show(msg, ok ? "Force Kick Completed" : "Force Kick Failed",
            MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Error);

        await RefreshAsync();
    }

    private bool CanRemoveStaleLock() =>
        SelectedFile is { Availability: FileAvailability.Stale, CurrentLock: not null };

    // Clears a stale lock (no heartbeat for SharedFileLockStore.StaleAfter) without the
    // "kick" semantics: nothing is sent to the holder's machine. Stale locks otherwise
    // linger in the shared-folder file until someone opens that exact file.
    [RelayCommand(CanExecute = nameof(CanRemoveStaleLock))]
    public async Task RemoveStaleLockAsync()
    {
        var file = SelectedFile;
        var stale = file?.CurrentLock;
        if (file == null || stale == null || file.Availability != FileAvailability.Stale) return;

        var idle = DateTime.UtcNow - stale.LastHeartbeatUtc;
        var idleText = idle.TotalHours >= 1
            ? $"{(int)idle.TotalHours} hour(s)"
            : $"{Math.Max(1, (int)idle.TotalMinutes)} minute(s)";

        var confirm = MessageBox.Show(
            $"{stale.UserName}'s lock on {file.FileName} (computer {stale.MachineName}) has had no activity for {idleText}, so they are probably not using it.\n\nRemove this stale lock?\n\nMake sure {stale.UserName} is not still working in the file.",
            "Remove Stale Lock", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        await _fileStore.RemoveAsync(stale.LockId);
        await RefreshAsync();

        // The server expires its own stale locks within a minute (and Open takes one over
        // immediately), so it can still list this one for a moment after we cleared ours.
        var stillListed = Files.Any(f =>
            f.FileKey.Equals(file.FileKey, StringComparison.OrdinalIgnoreCase) &&
            f.Availability == FileAvailability.Stale);
        StatusMessage = stillListed
            ? $"Removed the backup record for {file.FileName}. The server still lists it as stale and will clear it within a minute — or just click Open."
            : $"Stale lock on {file.FileName} removed.";
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
            ApplySettingsLive();
            _ = RefreshAsync();
        }
    }

    // Pushes settings that are baked into long-lived objects (HttpClient, timers)
    // out to those objects immediately, so no restart is needed.
    private void ApplySettingsLive()
    {
        _lockClient.UpdateConnection(_settings.LockServiceBaseUrl, _settings.ApiKey, _settings.ServiceTimeoutSeconds);
        _heartbeat.UpdateInterval(_settings.HeartbeatIntervalSeconds);
    }

    // Applies a dropped qblm-connect.json file's server/key immediately.
    public async Task ApplyConnectionProfileAsync(Models.ConnectionProfile profile)
    {
        _settings.LockServiceBaseUrl = profile.ServerUrl;
        _settings.ApiKey = profile.ApiKey;
        new SettingsService().Save(_settings);
        ApplySettingsLive();
        StatusMessage = "Connection settings updated.";
        await RefreshAsync();
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

        // Ask the admin whether they want to open the file now.
        MessageBoxResult open = MessageBoxResult.No;
        Application.Current.Dispatcher.Invoke(() =>
        {
            var fileName = fileVm?.FileName ?? "the file";
            open = MessageBox.Show(
                $"QuickBooks has been closed on the locked computer.\n\nWould you like to open {fileName} now?",
                "Lock Released", MessageBoxButton.YesNo, MessageBoxImage.Question);
        });

        if (open != MessageBoxResult.Yes) return;

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

            result.Lock.AppInstanceId = _appInstanceId;
            result.Lock.LastHeartbeatUtc = DateTime.UtcNow;
            await _fileStore.UpsertAsync(result.Lock);

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
                await _heartbeat.ReleaseLockAsync(result.Lock.LockId);
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
                StatusMessage = $"Error opening file: {ex.Message}");
        }
        finally
        {
            Application.Current.Dispatcher.Invoke(() => IsBusy = false);
            await RefreshAsync();
        }
    }

    private void OnLockLost(string lockId)
    {
        // The server rejected the lock (expired or force-released) — drop the mirror too,
        // so the shared-folder file doesn't keep blocking whoever the server handed it to.
        _heartbeat.UnregisterLock(lockId);
        _ = _fileStore.RemoveAsync(lockId);
        Application.Current.Dispatcher.Invoke(() =>
        {
            StatusMessage = $"WARNING: The server no longer recognizes your lock (it may have expired or been force-released). Please verify your file status and re-acquire if needed.";
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
