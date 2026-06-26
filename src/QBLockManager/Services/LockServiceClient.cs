using System.Net.Http;
using System.Net.Http.Json;
using QBLockManager.Models;

namespace QBLockManager.Services;

public class AcquireResult
{
    public bool Success { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Message { get; set; }
    public LockInfoDto? Lock { get; set; }
    public LockInfoDto? CurrentHolder { get; set; }
}

public class LockServiceClient
{
    private readonly HttpClient _http;

    public LockServiceClient(string baseUrl, string apiKey, int timeoutSeconds = 10)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(NormalizeUrl(baseUrl)),
            Timeout = TimeSpan.FromSeconds(timeoutSeconds)
        };

        if (!string.IsNullOrWhiteSpace(apiKey))
            _http.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
    }

    private static string NormalizeUrl(string url)
    {
        url = url.Trim().TrimEnd('/');
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = "http://" + url;
        return url + "/";
    }

    public async Task<bool> IsReachableAsync()
    {
        try
        {
            // Use an authenticated endpoint so a wrong API key shows as unreachable
            // rather than falsely appearing connected. /health skips auth.
            var resp = await _http.GetAsync("api/files/locks");
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<List<LockInfoDto>> GetAllLocksAsync()
    {
        var resp = await _http.GetAsync("api/files/locks");
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<List<LockInfoDto>>() ?? new();
    }

    public async Task<List<LockInfoDto>> GetMyLocksAsync(string appInstanceId)
    {
        var resp = await _http.GetAsync($"api/locks/mine?appInstanceId={Uri.EscapeDataString(appInstanceId)}");
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<List<LockInfoDto>>() ?? new();
    }

    public async Task<AcquireResult> AcquireAsync(
        string fileKey, string fileName, string localPath,
        string userName, string? displayName, string? email,
        string machineName, string appInstanceId)
    {
        var body = new
        {
            fileKey, fileName, localPath, userName, displayName, email, machineName, appInstanceId
        };

        var resp = await _http.PostAsJsonAsync("api/locks/acquire", body);

        if (resp.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            var denied = await resp.Content.ReadFromJsonAsync<AcquireResult>();
            return denied ?? new AcquireResult { Success = false, Status = "Denied", Message = "Lock denied." };
        }

        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<AcquireResult>()
               ?? new AcquireResult { Success = false, Status = "Error" };
    }

    public async Task<bool> HeartbeatAsync(string lockId, string appInstanceId, DateTime? fileModifiedAtUtc = null)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync("api/locks/heartbeat", new { lockId, appInstanceId, fileModifiedAtUtc });
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> ReleaseAsync(string lockId, string appInstanceId)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync("api/locks/release", new { lockId, appInstanceId });
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<(bool Success, string Message)> ForceReleaseAsync(
        string lockId, string adminUserName, string? reason, string? adminAppInstanceId = null)
    {
        var resp = await _http.PostAsJsonAsync("api/locks/force-release",
            new { lockId, adminUserName, reason, adminAppInstanceId });
        var body = await resp.Content.ReadFromJsonAsync<ReleaseResultDto>();
        var msg = body?.Message ?? (resp.IsSuccessStatusCode ? "Done." : "Failed.");
        return (resp.IsSuccessStatusCode, msg);
    }

    public async Task<List<PendingCommandDto>> PollCommandsAsync(string appInstanceId)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync("api/commands/poll", new { appInstanceId });
            if (!resp.IsSuccessStatusCode) return new();
            return await resp.Content.ReadFromJsonAsync<List<PendingCommandDto>>() ?? new();
        }
        catch { return new(); }
    }

    public async Task<List<AuditLogEntryDto>> GetAuditLogAsync(int limit = 100, string? fileKey = null)
    {
        var url = $"api/audit?limit={limit}";
        if (!string.IsNullOrEmpty(fileKey)) url += $"&fileKey={Uri.EscapeDataString(fileKey)}";
        var resp = await _http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<List<AuditLogEntryDto>>() ?? new();
    }
}

public class ReleaseResultDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class PendingCommandDto
{
    public long CommandId { get; set; }
    public string Command { get; set; } = string.Empty;
    public string? FileKey { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public class AuditLogEntryDto
{
    public long AuditId { get; set; }
    public DateTime TimestampUtc { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string? FileKey { get; set; }
    public string? FileName { get; set; }
    public string? UserName { get; set; }
    public string? MachineName { get; set; }
    public string? LockId { get; set; }
    public string? Details { get; set; }
}
