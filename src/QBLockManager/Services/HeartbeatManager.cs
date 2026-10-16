using System.Collections.Concurrent;
using System.IO;

namespace QBLockManager.Services;

public class ActiveFileLock
{
    public string LockId { get; init; } = string.Empty;
    public string FileKey { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string LocalPath { get; init; } = string.Empty;
    public DateTime AcquiredAt { get; init; } = DateTime.UtcNow;
    // Acquired while the lock service was unreachable — lives only in the shared-folder file.
    public bool OfflineOnly { get; init; }
}

public class HeartbeatManager : IDisposable
{
    private readonly LockServiceClient _client;
    private readonly SharedFileLockStore _fileStore;
    private readonly string _appInstanceId;
    private readonly ConcurrentDictionary<string, ActiveFileLock> _activeLocks = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastWriteTimes = new();
    private readonly System.Timers.Timer _timer;
    private bool _disposed;

    public event Action<string>? LockLost;
    public event Action? CloseRequested;
    public event Action<string>? OpenFileRequested; // fileKey

    public IReadOnlyDictionary<string, ActiveFileLock> ActiveLocks => _activeLocks;

    public HeartbeatManager(LockServiceClient client, SharedFileLockStore fileStore,
        string appInstanceId, int intervalSeconds = 20)
    {
        _client = client;
        _fileStore = fileStore;
        _appInstanceId = appInstanceId;

        _timer = new System.Timers.Timer(intervalSeconds * 1000);
        _timer.Elapsed += async (_, _) => await SendHeartbeatsAsync();
        _timer.AutoReset = true;
        _timer.Start();
    }

    // Applies a new heartbeat interval immediately — no app restart required.
    public void UpdateInterval(int intervalSeconds)
    {
        _timer.Interval = intervalSeconds * 1000;
    }

    public void RegisterLock(ActiveFileLock fileLock)
    {
        _activeLocks[fileLock.LockId] = fileLock;
        // Snapshot the current write time so the first heartbeat doesn't fire a false FileModified.
        if (!string.IsNullOrEmpty(fileLock.LocalPath) && File.Exists(fileLock.LocalPath))
            _lastWriteTimes[fileLock.LockId] = File.GetLastWriteTimeUtc(fileLock.LocalPath);
    }

    public void UnregisterLock(string lockId)
    {
        _activeLocks.TryRemove(lockId, out _);
        _lastWriteTimes.TryRemove(lockId, out _);
    }

    public bool HasActiveLock(string fileKey)
        => _activeLocks.Values.Any(l => l.FileKey.Equals(fileKey, StringComparison.OrdinalIgnoreCase));

    public ActiveFileLock? GetLockForFile(string fileKey)
        => _activeLocks.Values.FirstOrDefault(l => l.FileKey.Equals(fileKey, StringComparison.OrdinalIgnoreCase));

    // Releases one lock everywhere it lives: the server (if it knows it), the shared file,
    // and the local registry. Returns whether the server accepted the release (true for
    // offline-only locks, which the server never knew about).
    public async Task<bool> ReleaseLockAsync(string lockId)
    {
        _activeLocks.TryGetValue(lockId, out var fileLock);
        var ok = true;
        if (fileLock is { OfflineOnly: false })
            ok = await _client.ReleaseAsync(lockId, _appInstanceId);
        await _fileStore.RemoveAsync(lockId);
        UnregisterLock(lockId);
        return ok;
    }

    private async Task SendHeartbeatsAsync()
    {
        var held = _activeLocks.ToList();

        foreach (var (lockId, fileLock) in held)
        {
            if (fileLock.OfflineOnly) continue; // the server doesn't know this lock

            DateTime? fileModifiedAtUtc = null;

            if (!string.IsNullOrEmpty(fileLock.LocalPath) && File.Exists(fileLock.LocalPath))
            {
                var currentWriteTime = File.GetLastWriteTimeUtc(fileLock.LocalPath);
                _lastWriteTimes.TryGetValue(lockId, out var previousWriteTime);

                if (currentWriteTime != previousWriteTime)
                {
                    fileModifiedAtUtc = currentWriteTime;
                    _lastWriteTimes[lockId] = currentWriteTime;
                }
            }

            // null = server unreachable: keep the lock, the shared file carries it meanwhile.
            var result = await _client.HeartbeatAsync(lockId, _appInstanceId, fileModifiedAtUtc);
            if (result == false)
                LockLost?.Invoke(lockId);
        }

        // Keep the shared-folder mirror fresh so other machines don't see our locks as stale.
        await _fileStore.TouchAsync(held.Select(kv => kv.Key).ToList());

        // Check for commands queued by the server (e.g. admin force-close or auto-open).
        var commands = await _client.PollCommandsAsync(_appInstanceId);
        foreach (var cmd in commands)
        {
            if (cmd.Command == "CloseQuickBooks")
                CloseRequested?.Invoke();
            else if (cmd.Command == "OpenFile" && !string.IsNullOrEmpty(cmd.FileKey))
                OpenFileRequested?.Invoke(cmd.FileKey);
        }
    }

    public async Task ReleaseAllAsync()
    {
        foreach (var (lockId, _) in _activeLocks.ToList())
            await ReleaseLockAsync(lockId);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Dispose();
    }
}
