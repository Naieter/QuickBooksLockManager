using System.IO;
using System.Text.Json;
using QBLockManager.Models;

namespace QBLockManager.Services;

public class SettingsService
{
    // User-writable location — C:\Program Files is read-only for non-admins
    private static readonly string DefaultConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "QBLockManager");

    // Fallback: installer writes initial config here but we can't write back to it
    private static readonly string DefaultInstallerConfigPath = Path.Combine(
        AppContext.BaseDirectory, "appsettings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _configDir;
    private readonly string _configPath;
    private readonly string _installerConfigPath;

    public SettingsService() : this(DefaultConfigDir, DefaultInstallerConfigPath) { }

    // Directories are injectable so the settings-protection logic can be tested without touching
    // a real profile.
    public SettingsService(string configDir, string installerConfigPath)
    {
        _configDir = configDir;
        _configPath = Path.Combine(configDir, "appsettings.json");
        _installerConfigPath = installerConfigPath;
    }

    // Set by Load() when an existing settings file could not be read.
    public string? LoadWarning { get; private set; }

    public AppSettings Load()
    {
        LoadWarning = null;

        // Prefer user config; fall back to installer-written config
        string? pathToLoad = File.Exists(_configPath) ? _configPath
            : File.Exists(_installerConfigPath) ? _installerConfigPath
            : null;

        if (pathToLoad == null)
        {
            var defaults = new AppSettings();
            Save(defaults);
            return defaults;
        }

        var settings = TryRead(pathToLoad);
        if (settings != null) return settings;

        // Unreadable. Blank defaults must never quietly replace someone's real settings (the next
        // save would overwrite them), so keep a copy of the original first and say so.
        var saved = PreserveUnreadable(pathToLoad);
        LoadWarning = "Your settings file couldn't be read, so the program is starting with blank settings."
            + (saved != null ? $"\n\nA copy of the original was kept so nothing is lost:\n{saved}" : "");
        return new AppSettings();
    }

    // Writes via a temp file and a rename, so a crash or power loss mid-save can't leave a
    // half-written (unreadable) settings file behind.
    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(_configDir);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        var tmp = _configPath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _configPath, overwrite: true);
    }

    // True when the settings are complete enough to run: a server address and at least one watched
    // folder. Deliberately NOT based on SetupCompleted, which configs written by the old installer
    // don't have; treating those as "needs setup" would send a working install through the wizard.
    public static bool IsConfigured(AppSettings s) =>
        !string.IsNullOrWhiteSpace(s.LockServiceBaseUrl) &&
        s.WatchedFolders?.Any(f => !string.IsNullOrWhiteSpace(f.Path)) == true;

    // "1.6.11" for 1.6.11.0 — the form used in backup file names and the last-version marker.
    public static string VersionKey(Version v) => $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";

    // ── Update protection ───────────────────────────────────────────────────────

    // Called just before an update is applied. Keeps a copy of the settings as they are now, named
    // for the version about to be installed; the three most recent are kept.
    public string? BackupBeforeUpdate(Version newVersion)
    {
        try
        {
            if (!File.Exists(_configPath)) return null;
            var dest = Path.Combine(_configDir, $"appsettings.before-{VersionKey(newVersion)}.json");
            File.Copy(_configPath, dest, overwrite: true);

            // Keep the three newest by VERSION (not file time: File.Copy preserves the source's
            // timestamp, so every backup would tie), and never touch the one just written.
            foreach (var old in new DirectoryInfo(_configDir).GetFiles("appsettings.before-*.json")
                         .Where(f => !string.Equals(f.FullName, dest, StringComparison.OrdinalIgnoreCase))
                         .OrderByDescending(f => BackupVersion(f.Name))
                         .Skip(2))
                try { old.Delete(); } catch { }
            return dest;
        }
        catch { return null; }
    }

    // Called once at startup. On the first run of a different version, compares the four settings an
    // update must never change (QuickBooks path, QuickBooks file password, watched folders, shared
    // root) with the copy taken just before the update, and puts back any that differ. Nothing the
    // user does can have happened in between, so a difference is always the update's doing.
    // Returns the names of what was restored (empty = nothing needed fixing).
    public IReadOnlyList<string> ReconcileAfterUpdate(AppSettings loaded, Version current)
    {
        var key = VersionKey(current);
        var versionFile = Path.Combine(_configDir, "lastversion.txt");

        string? last = null;
        try { if (File.Exists(versionFile)) last = File.ReadAllText(versionFile).Trim(); } catch { }

        var restored = new List<string>();
        if (last != null && last != key)
        {
            var before = TryRead(Path.Combine(_configDir, $"appsettings.before-{key}.json"));
            if (before != null)
            {
                restored.AddRange(CopyProtectedFields(before, loaded));
                if (restored.Count > 0) Save(loaded);
            }
        }

        try { Directory.CreateDirectory(_configDir); File.WriteAllText(versionFile, key); } catch { }
        return restored;
    }

    // Copies the protected settings from -> to wherever they differ; returns what changed.
    internal static List<string> CopyProtectedFields(AppSettings from, AppSettings to)
    {
        var changed = new List<string>();
        if (to.QuickBooksExePath != from.QuickBooksExePath)
        { to.QuickBooksExePath = from.QuickBooksExePath; changed.Add("QuickBooks executable path"); }
        if (to.QuickBooksPassword != from.QuickBooksPassword)
        { to.QuickBooksPassword = from.QuickBooksPassword; changed.Add("QuickBooks file password"); }
        if (!SameFolders(to.WatchedFolders, from.WatchedFolders))
        {
            to.WatchedFolders = (from.WatchedFolders ?? new())
                .Select(f => new WatchedFolder { Path = f.Path, Recursive = f.Recursive, Label = f.Label }).ToList();
            changed.Add("watched folders");
        }
        if (to.SharedRootPath != from.SharedRootPath)
        { to.SharedRootPath = from.SharedRootPath; changed.Add("shared root path"); }
        return changed;
    }

    internal static bool SameFolders(List<WatchedFolder>? a, List<WatchedFolder>? b)
    {
        a ??= new(); b ??= new();
        return a.Count == b.Count && a.Zip(b).All(p =>
            p.First.Path == p.Second.Path && p.First.Recursive == p.Second.Recursive && p.First.Label == p.Second.Label);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    // "appsettings.before-1.6.11.json" -> 1.6.11 (0.0 if it can't be read, so it sorts oldest).
    private static Version BackupVersion(string fileName)
    {
        var core = fileName["appsettings.before-".Length..^".json".Length];
        return Version.TryParse(core, out var v) ? v : new Version(0, 0);
    }

    private static AppSettings? TryRead(string path)
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions); }
        catch { return null; }
    }

    private string? PreserveUnreadable(string source)
    {
        try
        {
            Directory.CreateDirectory(_configDir);
            var dest = Path.Combine(_configDir, $"appsettings.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Copy(source, dest, overwrite: true);
            return dest;
        }
        catch { return null; }
    }

    public static ConnectionProfile? LoadConnectionProfile()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "qblm-connect.json");
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            var p = JsonSerializer.Deserialize<ConnectionProfile>(json, JsonOptions);
            return p != null && !string.IsNullOrWhiteSpace(p.ServerUrl) ? p : null;
        }
        catch { return null; }
    }
}
