using System.IO;

namespace QBLockManager.Services;

// Google Drive (and other cloud-sync clients) can remount under a different drive
// letter after a reboot or reconnect. Paths are stored with whatever letter was
// current at setup time, so this re-locates the same folder if that letter no
// longer resolves — trying the configured drive first, then a small in-memory
// cache, then every other ready drive for an identical sub-path match.
public static class DriveLetterResolver
{
    private static readonly Dictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string Resolve(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath) || configuredPath.Length < 3 || configuredPath[1] != ':')
            return configuredPath;

        if (Directory.Exists(configuredPath))
        {
            Cache[configuredPath] = configuredPath;
            return configuredPath;
        }

        if (Cache.TryGetValue(configuredPath, out var cached) && Directory.Exists(cached))
            return cached;

        var subPath = configuredPath[2..]; // everything after "X:"
        var configuredDrive = configuredPath[..1];

        foreach (var drive in DriveInfo.GetDrives())
        {
            // Only probe fixed drives (Google Drive mounts as Fixed). Network/CD drives
            // can block IsReady/Exists for many seconds when dead or disconnected.
            if (drive.DriveType != DriveType.Fixed) continue;
            if (!drive.IsReady) continue;
            var letter = drive.Name[..1];
            if (letter.Equals(configuredDrive, StringComparison.OrdinalIgnoreCase)) continue;

            var candidate = letter + ":" + subPath;
            if (Directory.Exists(candidate))
            {
                Cache[configuredPath] = candidate;
                return candidate;
            }
        }

        // Nothing found — return the original so callers' existing "not available" handling applies.
        return configuredPath;
    }
}
