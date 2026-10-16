using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;

namespace QBLockServiceSetup;

// Builds the qblm-connect.json that workstations import (drag-and-drop) or pick up
// automatically from <shared QB folder>\QBLockCert. Same format the client reads.
internal static class ConnectionFile
{
    // "203.0.113.7" -> "http://203.0.113.7:5100". A plain-http address with no port gets the
    // server's port (the service listens on it); an https address is left alone.
    public static string NormalizeAddress(string address, string port)
    {
        var s = address.Trim().TrimEnd('/');
        if (s.Length == 0) return "";

        if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            s = "http://" + s;

        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            var authorityStart = s.IndexOf("://", StringComparison.Ordinal) + 3;
            var authorityEnd = s.IndexOf('/', authorityStart);
            if (authorityEnd < 0) authorityEnd = s.Length;
            if (!s.AsSpan(authorityStart, authorityEnd - authorityStart).Contains(':'))
                s = s.Insert(authorityEnd, ":" + port.Trim());
        }
        return s;
    }

    public static string ToJson(string serverUrl, string apiKey) =>
        JsonSerializer.Serialize(new { ServerUrl = serverUrl, ApiKey = apiKey },
            new JsonSerializerOptions { WriteIndented = true });

    private static readonly string[] PublicIpEndpoints =
    {
        "https://api.ipify.org",
        "https://checkip.amazonaws.com",
        "https://icanhazip.com"
    };

    // Workstations reach the server through its PUBLIC address (the router forwards the port),
    // and that address changes when the ISP re-issues one. Ask a what's-my-IP service for it.
    // Only a plain IPv4 answer is accepted, so a captive-portal page can't become the address.
    // Returns "http://<ip>:<port>", or null if nothing answered. Endpoints are injectable for tests.
    public static async Task<string?> DetectPublicAddressAsync(
        string port, IEnumerable<string>? endpoints = null, int timeoutSeconds = 5)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
        foreach (var url in endpoints ?? PublicIpEndpoints)
        {
            try
            {
                var text = (await http.GetStringAsync(url)).Trim();
                if (IPAddress.TryParse(text, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
                    return $"http://{ip}:{port}";
            }
            catch { /* unreachable or odd response: try the next one */ }
        }
        return null;
    }

    // Which address to show: a saved DNS name is deliberate and stable, so it wins; a saved raw
    // IP may be stale after the ISP changes it, so a freshly detected address replaces it.
    public static string ChooseClientAddress(string? stored, string? detected)
    {
        if (!string.IsNullOrWhiteSpace(stored) && HostIsName(stored)) return stored;
        return detected ?? stored ?? "";
    }

    private static bool HostIsName(string address)
    {
        var s = address.Trim();
        var i = s.IndexOf("://", StringComparison.Ordinal);
        if (i >= 0) s = s[(i + 3)..];
        var host = s.Split('/', ':')[0];
        return host.Length > 0 && !IPAddress.TryParse(host, out _);
    }
}
