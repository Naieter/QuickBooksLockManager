using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QBLockManager.Services;

public record UpdateInfo(string TagName, Version Version, string DownloadUrl, long SizeBytes);

public static class UpdateChecker
{
    private const string Repo      = "Naieter/QuickBooksLockManager";
    private const string AssetName = "QBLockManager.exe";
    private const string ApiUrl    = "https://api.github.com/repos/" + Repo + "/releases/latest";

    public static Version CurrentVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0);

    // Returns UpdateInfo if a newer release exists on GitHub, null otherwise.
    // Fails silently — a failed check never blocks startup.
    public static async Task<UpdateInfo?> CheckAsync(string? githubToken = null)
    {
        try
        {
            using var http = BuildApiClient(githubToken);
            http.Timeout = TimeSpan.FromSeconds(8);

            var json = await http.GetStringAsync(ApiUrl);
            var release = JsonSerializer.Deserialize<GhRelease>(json);
            if (release == null) return null;

            var latestVersion = ParseVersion(release.TagName);
            if (latestVersion == null || latestVersion <= CurrentVersion) return null;

            var asset = release.Assets.FirstOrDefault(a =>
                string.Equals(a.Name, AssetName, StringComparison.OrdinalIgnoreCase));
            if (asset == null) return null;

            return new UpdateInfo(release.TagName, latestVersion, asset.DownloadUrl, asset.Size);
        }
        catch
        {
            return null;
        }
    }

    public static async Task DownloadAsync(
        UpdateInfo update,
        string destPath,
        string? githubToken,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        // GitHub private-asset URLs redirect to S3 presigned URLs.
        // Follow the redirect manually so we can drop the auth header before hitting S3
        // (S3 presigned URLs embed their own auth; sending an extra Authorization header
        // causes a 400 error from AWS).
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var http = new HttpClient(handler);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("QBLockManager-Updater");
        if (!string.IsNullOrWhiteSpace(githubToken))
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", githubToken);

        string downloadUrl = update.DownloadUrl;

        // Resolve any redirect (GitHub → S3)
        using (var probe = await http.GetAsync(downloadUrl,
                   HttpCompletionOption.ResponseHeadersRead, ct))
        {
            if ((int)probe.StatusCode is 301 or 302 or 307 or 308)
            {
                downloadUrl = probe.Headers.Location?.ToString() ?? downloadUrl;
                http.DefaultRequestHeaders.Authorization = null;
            }
        }

        using var resp = await http.GetAsync(downloadUrl,
            HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength ?? update.SizeBytes;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write,
            FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long bytesRead = 0;
        int read;
        while ((read = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
            bytesRead += read;
            if (total > 0) progress?.Report((double)bytesRead / total);
        }
    }

    // Launches a hidden PowerShell process that:
    //   1. Waits for this process to exit
    //   2. Replaces installedExePath with downloadedExePath
    //   3. Launches the new exe
    // The entire script is base64-encoded so file paths with spaces/special characters work.
    public static void LaunchUpdater(string installedExePath, string downloadedExePath)
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
if (-not (Test-Path -LiteralPath $tgt)) {{ Copy-Item -Force -LiteralPath $src -Destination $tgt }}
Remove-Item -LiteralPath $src -Force -ErrorAction SilentlyContinue
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

    private static HttpClient BuildApiClient(string? token)
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("QBLockManager-Updater");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        if (!string.IsNullOrWhiteSpace(token))
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    private static Version? ParseVersion(string tag)
    {
        var s = tag.TrimStart('v', 'V');
        if (!s.Contains('.')) s += ".0";
        if (!Version.TryParse(s, out var v)) return null;
        // Normalize to 4 components so GitHub 3-part tags (1.4.1 → revision=-1)
        // compare correctly against 4-part assembly versions (1.4.1.0 → revision=0).
        return new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
    }

    // ── GitHub API response models ────────────────────────────────────────────

    private class GhRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
        [JsonPropertyName("assets")]   public List<GhAsset> Assets { get; set; } = new();
    }

    private class GhAsset
    {
        [JsonPropertyName("name")]                 public string Name        { get; set; } = "";
        [JsonPropertyName("browser_download_url")] public string DownloadUrl { get; set; } = "";
        [JsonPropertyName("size")]                 public long   Size        { get; set; }
    }
}
