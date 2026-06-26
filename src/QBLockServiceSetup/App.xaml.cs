using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;

namespace QBLockServiceSetup;

public partial class App : Application
{
    private const string Repo      = "Naieter/QuickBooksLockManager";
    private const string AssetName = "QBLockService-Setup.exe";
    private const string ApiUrl    = $"https://api.github.com/repos/{Repo}/releases/latest";

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (await TryApplyUpdateAsync())
        {
            Shutdown();
            return;
        }

        new MainWindow().Show();
    }

    private static async Task<bool> TryApplyUpdateAsync()
    {
        var update = await CheckAsync();
        if (update == null) return false;

        var result = MessageBox.Show(
            $"Update available: {update.TagName}\n\nDownload and install now?",
            "Setup Update Available", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (result != MessageBoxResult.Yes) return false;

        var tempPath = Path.Combine(Path.GetTempPath(), AssetName);
        try
        {
            await DownloadAsync(update, tempPath);
            LaunchUpdater(Process.GetCurrentProcess().MainModule!.FileName!, tempPath);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Update download failed:\n{ex.Message}", "Update Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private static async Task<UpdateInfo?> CheckAsync()
    {
        try
        {
            using var http = BuildApiClient();
            http.Timeout = TimeSpan.FromSeconds(8);
            var json = await http.GetStringAsync(ApiUrl);
            var release = JsonSerializer.Deserialize<GhRelease>(json);
            if (release == null) return null;

            var latestVersion = ParseVersion(release.TagName);
            if (latestVersion == null || latestVersion <= CurrentVersion) return null;

            var asset = release.Assets.FirstOrDefault(a =>
                string.Equals(a.Name, AssetName, StringComparison.OrdinalIgnoreCase));
            if (asset == null) return null;

            return new UpdateInfo(release.TagName, latestVersion, asset.DownloadUrl);
        }
        catch { return null; }
    }

    private static async Task DownloadAsync(UpdateInfo update, string destPath)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var http = new HttpClient(handler);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("QBLockService-Setup-Updater");

        string downloadUrl = update.DownloadUrl;
        using (var probe = await http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            if ((int)probe.StatusCode is 301 or 302 or 307 or 308)
            {
                downloadUrl = probe.Headers.Location?.ToString() ?? downloadUrl;
                http.DefaultRequestHeaders.Authorization = null;
            }
        }

        using var resp = await http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        await using var src = await resp.Content.ReadAsStreamAsync();
        await using var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write);
        await src.CopyToAsync(dst);
    }

    private static void LaunchUpdater(string installedExePath, string downloadedExePath)
    {
        var pid    = Process.GetCurrentProcess().Id;
        var srcB64 = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(downloadedExePath));
        var tgtB64 = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(installedExePath));

        var script = $@"
$src = [System.Text.Encoding]::Unicode.GetString([System.Convert]::FromBase64String('{srcB64}'))
$tgt = [System.Text.Encoding]::Unicode.GetString([System.Convert]::FromBase64String('{tgtB64}'))
while (Get-Process -Id {pid} -ErrorAction SilentlyContinue) {{ Start-Sleep -Milliseconds 500 }}
Start-Sleep -Milliseconds 500
Move-Item -Force -LiteralPath $src -Destination $tgt
Start-Process -FilePath $tgt
";
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        Process.Start(new ProcessStartInfo
        {
            FileName        = "powershell.exe",
            Arguments       = $"-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            UseShellExecute = false,
            CreateNoWindow  = true
        });
    }

    private static Version CurrentVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0);

    private static Version? ParseVersion(string tag)
    {
        var s = tag.TrimStart('v', 'V');
        if (!s.Contains('.')) s += ".0";
        if (!Version.TryParse(s, out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
    }

    private static HttpClient BuildApiClient()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("QBLockService-Setup-Updater");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return http;
    }

    private record UpdateInfo(string TagName, Version Version, string DownloadUrl);

    private class GhRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
        [JsonPropertyName("assets")]   public List<GhAsset> Assets { get; set; } = new();
    }

    private class GhAsset
    {
        [JsonPropertyName("name")]                 public string Name        { get; set; } = "";
        [JsonPropertyName("browser_download_url")] public string DownloadUrl { get; set; } = "";
    }
}
