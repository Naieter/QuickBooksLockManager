using System.IO;
using System.Text.Json;
using QBLockManager.Models;

namespace QBLockManager.Services;

public class SettingsService
{
    // User-writable location — C:\Program Files is read-only for non-admins
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "QBLockManager");

    private static readonly string ConfigPath = Path.Combine(ConfigDir, "appsettings.json");

    // Fallback: installer writes initial config here but we can't write back to it
    private static readonly string InstallerConfigPath = Path.Combine(
        AppContext.BaseDirectory, "appsettings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public AppSettings Load()
    {
        // Prefer user config; fall back to installer-written config
        string? pathToLoad = File.Exists(ConfigPath) ? ConfigPath
            : File.Exists(InstallerConfigPath) ? InstallerConfigPath
            : null;

        if (pathToLoad == null)
        {
            var defaults = new AppSettings();
            Save(defaults);
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(pathToLoad);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(ConfigDir);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(ConfigPath, json);
    }
}
