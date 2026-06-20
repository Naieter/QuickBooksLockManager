using System.Collections.Concurrent;

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
    private readonly System.Timers.Timer _timer;
    private bool _disposed;

    public event Action<string>? LockLost;

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
    }

    public void UnregisterLock(string lockId)
    {
        _activeLocks.TryRemove(lockId, out _);
    }

    public bool HasActiveLock(string fileKey)
        => _activeLocks.Values.Any(l => l.FileKey.Equals(fileKey, StringComparison.OrdinalIgnoreCase));

    public ActiveFileLock? GetLockForFile(string fileKey)
        => _activeLocks.Values.FirstOrDefault(l => l.FileKey.Equals(fileKey, StringComparison.OrdinalIgnoreCase));

    private async Task SendHeartbeatsAsync()
    {
        foreach (var (lockId, fileLock) in _activeLocks.ToList())
        {
            var ok = await _client.HeartbeatAsync(lockId, _appInstanceId);
            if (!ok)
            {
                // Heartbeat failed — lock may have expired on server
                LockLost?.Invoke(lockId);
            }
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
