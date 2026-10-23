using System.IO;
using QBLockManager.Models;

namespace QBLockManager.Services;

public static class SettingsMerge
{
    // What the first-run wizard produces when applied ON TOP of whatever settings already exist.
    // It used to build a brand-new AppSettings, which dropped the QuickBooks password, replaced the
    // QuickBooks path and reset every option the wizard doesn't ask about. Now it only sets what the
    // wizard actually collected, and leaves everything else (and anything the admin kept) alone.
    // Note: modifies and returns `existing` itself when one is given.
    public static AppSettings ForFirstRun(
        AppSettings? existing, string serverUrl, string apiKey, string folder,
        string? detectedQbExe, string windowsUser, string displayName,
        Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        var s = existing ?? new AppSettings();

        s.SetupCompleted = true;
        s.LockServiceBaseUrl = serverUrl;
        s.ApiKey = apiKey;

        // Keep a QuickBooks path that works; use the detected one only when there isn't one.
        if (detectedQbExe != null &&
            (string.IsNullOrWhiteSpace(s.QuickBooksExePath) || !fileExists(s.QuickBooksExePath)))
            s.QuickBooksExePath = detectedQbExe;

        // Same folder as already configured: leave the watched-folder list (recursion, labels,
        // extra folders) and the shared root exactly as they are. A different folder is the
        // admin's explicit choice.
        var configured = s.WatchedFolders?.FirstOrDefault(f => !string.IsNullOrWhiteSpace(f.Path))?.Path;
        if (configured == null || !string.Equals(configured.Trim(), folder.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            s.WatchedFolders = new List<WatchedFolder>
            {
                new() { Path = folder, Recursive = false, Label = "QuickBooks Files" }
            };
            s.SharedRootPath = folder;
        }

        if (string.IsNullOrWhiteSpace(s.UserName)) s.UserName = windowsUser;
        if (string.IsNullOrWhiteSpace(s.DisplayName)) s.DisplayName = displayName;
        return s;
    }
}
