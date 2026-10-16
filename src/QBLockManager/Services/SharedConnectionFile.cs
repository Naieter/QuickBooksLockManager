using System.IO;
using System.Text.Json;
using QBLockManager.Models;

namespace QBLockManager.Services;

// Admin-published connection settings, shared through the same Google Drive folder as the
// company files: <watched folder>\QBLockCert\qblm-connect.json. When the lock server moves
// or its key changes, the admin drops the new file there once instead of visiting every PC.
public static class SharedConnectionFile
{
    public const string FolderName = "QBLockCert";
    public const string FileName = "qblm-connect.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // Returns the first valid profile found under any watched folder, or null.
    public static ConnectionProfile? Find(IEnumerable<string> watchedFolders)
    {
        foreach (var folder in watchedFolders)
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;

            // The Google Drive letter can change between runs, so resolve it each time.
            var path = Path.Combine(DriveLetterResolver.Resolve(folder), FolderName, FileName);
            var profile = TryRead(path);
            if (profile != null) return profile;
        }
        return null;
    }

    // The QBLockCert folder under the first watched folder that has one, so an exported file
    // can be saved straight where every workstation looks. Null if none exists yet.
    public static string? FindFolder(IEnumerable<string> watchedFolders)
    {
        foreach (var folder in watchedFolders)
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            var candidate = Path.Combine(DriveLetterResolver.Resolve(folder), FolderName);
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    // Writes a connection file in the same format Find reads and the drag-and-drop import accepts.
    public static void Write(string path, string serverUrl, string apiKey)
    {
        var profile = new ConnectionProfile
        {
            ServerUrl = LockServiceClient.NormalizeUrl(serverUrl).TrimEnd('/'),
            ApiKey = apiKey.Trim()
        };
        File.WriteAllText(path, JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static ConnectionProfile? TryRead(string path)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var p = JsonSerializer.Deserialize<ConnectionProfile>(File.ReadAllText(path), JsonOptions);
                return p != null && !string.IsNullOrWhiteSpace(p.ServerUrl) ? p : null;
            }
            catch (JsonException) { Thread.Sleep(200); }   // Drive can hand us a half-synced file
            catch (IOException) { Thread.Sleep(200); }
            catch (UnauthorizedAccessException) { return null; }
        }
        return null;
    }
}
