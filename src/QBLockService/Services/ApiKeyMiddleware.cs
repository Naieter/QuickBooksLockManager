namespace QBLockService.Services;

public class ApiKeyMiddleware
{
    private const string ApiKeyHeader = "X-Api-Key";
    private readonly RequestDelegate _next;
    private readonly IConfiguration _config;

    public ApiKeyMiddleware(RequestDelegate next, IConfiguration config)
    {
        _next = next;
        _config = config;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Skip auth for health and swagger
        var path = context.Request.Path.Value ?? "";
        if (path.StartsWith("/health", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var configuredKey = _config["ApiKey"];
        if (string.IsNullOrWhiteSpace(configuredKey))
        {
            // No key configured — allow all (development mode)
            await _next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue(ApiKeyHeader, out var providedKey) ||
            !string.Equals(configuredKey, providedKey, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Invalid or missing API key." });
            return;
        }

        await _next(context);
    }
}

public class AdminApiKeyMiddleware
{
    private const string AdminKeyHeader = "X-Admin-Key";
    private readonly RequestDelegate _next;
    private readonly IConfiguration _config;

    public AdminApiKeyMiddleware(RequestDelegate next, IConfiguration config)
    {
        _next = next;
        _config = config;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        if (!path.Contains("/force-release", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var adminKey = _config["AdminApiKey"];
        if (string.IsNullOrWhiteSpace(adminKey))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "Admin API key not configured. Force unlock is disabled." });
            return;
        }

        if (!context.Request.Headers.TryGetValue(AdminKeyHeader, out var providedKey) ||
            !string.Equals(adminKey, providedKey, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "Invalid or missing admin API key." });
            return;
        }

        await _next(context);
    }
}
