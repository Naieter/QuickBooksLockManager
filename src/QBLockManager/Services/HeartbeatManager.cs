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
}

public class HeartbeatManager : IDisposable
{
    private readonly LockServiceClient _client;
    private readonly string _appInstanceId;
    private readonly int _intervalSeconds;
    private readonly ConcurrentDictionary<string, ActiveFileLock> _activeLocks = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastWriteTimes = new();
    private readonly System.Timers.Timer _timer;
    private bool _disposed;

    public event Action<string>? LockLost;
    public event Action? CloseRequested;
    public event Action<string>? OpenFileRequested; // fileKey

    public IReadOnlyDictionary<string, ActiveFileLock> ActiveLocks => _activeLocks;

    public HeartbeatManager(LockServiceClient client, string appInstanceId, int intervalSeconds = 20)
    {
        _client = client;
        _appInstanceId = appInstanceId;
        _intervalSeconds = intervalSeconds;

        _timer = new System.Timers.Timer(intervalSeconds * 1000);
        _timer.Elapsed += async (_, _) => await SendHeartbeatsAsync();
        _timer.AutoReset = true;
        _timer.Start();
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

    private async Task SendHeartbeatsAsync()
    {
        foreach (var (lockId, fileLock) in _activeLocks.ToList())
        {
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

            var ok = await _client.HeartbeatAsync(lockId, _appInstanceId, fileModifiedAtUtc);
            if (!ok)
            {
                // Heartbeat failed — lock may have expired on server
                LockLost?.Invoke(lockId);
            }
        }

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
        {
            await _client.ReleaseAsync(lockId, _appInstanceId);
            _activeLocks.TryRemove(lockId, out _);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Dispose();
    }
}
