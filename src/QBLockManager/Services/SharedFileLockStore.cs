using System.IO;
using System.Text.Json;
using QBLockManager.Models;

namespace QBLockManager.Services;

// Backup "server": a JSON file in the shared QuickBooks folder. Google Drive syncs it
// between machines, so everyone sees each other's locks even when the lock service is
// unreachable. Not atomic — two machines can race — but good enough as a fallback.
// Online locks are mirrored here too, so a mid-session server outage doesn't hide them.
public class SharedFileLockStore
{
    public const string FileName = "QBLockManager-locks.json";
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);

    private readonly Func<string?> _folderProvider;
    private readonly object _sync = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    // The folder is resolved on every call — the drive letter can change between calls.
    public SharedFileLockStore(Func<string?> folderProvider)
    {
        _folderProvider = folderProvider;
    }

    public string? FilePath
    {
        get
        {
            var folder = _folderProvider();
            return string.IsNullOrWhiteSpace(folder) ? null : Path.Combine(folder, FileName);
        }
    }

    public bool IsAvailable
    {
        get
        {
            var folder = _folderProvider();
            return !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder);
        }
    }

    public Task<List<LockInfoDto>> ReadAsync() => Task.Run(Read);

    public Task UpsertAsync(LockInfoDto entry) => Task.Run(() => Modify(list =>
    {
        // Replace our own previous entry for this file, and sweep any stale entry for it —
        // taking over a stale lock means its previous holder is gone and won't clean up.
        list.RemoveAll(l => l.LockId == entry.LockId ||
                            l.FileKey.Equals(entry.FileKey, StringComparison.OrdinalIgnoreCase) &&
                            (l.AppInstanceId == entry.AppInstanceId || l.IsStale));
        list.Add(entry);
    }));

    public Task RemoveAsync(string lockId) => Task.Run(() => Modify(list =>
        list.RemoveAll(l => l.LockId == lockId)));

    // Heartbeat: refresh LastHeartbeatUtc for the given locks. No-op if none are present.
    public Task TouchAsync(IReadOnlyCollection<string> lockIds) => Task.Run(() =>
    {
        if (lockIds.Count == 0) return;
        Modify(list =>
        {
            var now = DateTime.UtcNow;
            foreach (var l in list.Where(l => lockIds.Contains(l.LockId)))
                l.LastHeartbeatUtc = now;
        });
    });

    private List<LockInfoDto> Read()
    {
        var path = FilePath;
        if (path == null || !File.Exists(path)) return new();

        lock (_sync)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    var json = File.ReadAllText(path);
                    var list = JsonSerializer.Deserialize<List<LockInfoDto>>(json, JsonOptions) ?? new();
                    var now = DateTime.UtcNow;
                    foreach (var l in list)
                        l.IsStale = now - l.LastHeartbeatUtc > StaleAfter;
                    return list;
                }
                catch (JsonException)
                {
                    // Google Drive can hand us a half-synced file; retry briefly, then give up.
                    Thread.Sleep(200);
                }
                catch (IOException)
                {
                    Thread.Sleep(200);
                }
            }
            return new();
        }
    }

    private void Modify(Action<List<LockInfoDto>> change)
    {
        var path = FilePath;
        if (path == null || !IsAvailable) return;

        lock (_sync)
        {
            var list = Read();
            change(list);
            // Housekeeping: entries nobody has heartbeated in a day are abandoned.
            var cutoff = DateTime.UtcNow.AddHours(-24);
            list.RemoveAll(l => l.LastHeartbeatUtc < cutoff);
            var json = JsonSerializer.Serialize(list, JsonOptions);

            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    // Write to a temp file then swap, so other machines never read a torn file.
                    var tmp = path + ".tmp";
                    File.WriteAllText(tmp, json);
                    try { File.Move(tmp, path, overwrite: true); }
                    catch (IOException)
                    {
                        File.WriteAllText(path, json);
                        try { File.Delete(tmp); } catch { }
                    }
                    return;
                }
                catch (IOException)
                {
                    Thread.Sleep(200);
                }
                catch (UnauthorizedAccessException)
                {
                    Thread.Sleep(200);
                }
            }
        }
    }
}
