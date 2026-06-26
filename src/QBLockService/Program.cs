using Microsoft.EntityFrameworkCore;
using QBLockService.Data;
using QBLockService.Models;
using QBLockService.Services;

var builder = WebApplication.CreateBuilder(args);

// Support running as a Windows Service (sc.exe) or as a normal console process.
builder.Host.UseWindowsService(options => options.ServiceName = "QBLockService");

// Services
builder.Services.AddDbContextFactory<AppDbContext>(options =>
{
    var dbPath = builder.Configuration["Database:Path"] ?? "qblocks.db";
    options.UseSqlite($"Data Source={dbPath}");
});

builder.Services.AddScoped<ILockService, LockService>();
builder.Services.AddHostedService<StaleChecker>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "QB Lock Service", Version = "v1" });
    c.AddSecurityDefinition("ApiKey", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Name = "X-Api-Key"
    });
});

var app = builder.Build();

// Set CWD to app directory before DB init so relative paths resolve correctly.
// UseWindowsService() only sets this inside app.Run(), which is too late.
Directory.SetCurrentDirectory(AppContext.BaseDirectory);

// Ensure DB schema exists on startup — idempotent via IF NOT EXISTS.
// We bypass EF Core migrations because MigrateAsync() is unreliable when running as
// a Windows Service (CWD timing, partial-index DDL ordering). Direct SQL is simpler.
using (var scope = app.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    await using var ctx = await factory.CreateDbContextAsync();

    logger.LogInformation("DB path: {Conn}", ctx.Database.GetConnectionString());

    // Ensure data directory exists before SQLite creates the file
    var connStr = ctx.Database.GetConnectionString() ?? "";
    var dsMatch = System.Text.RegularExpressions.Regex.Match(connStr, @"Data Source=([^;]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    if (dsMatch.Success)
    {
        var dir = Path.GetDirectoryName(dsMatch.Groups[1].Value);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    await ctx.Database.ExecuteSqlRawAsync(@"
        CREATE TABLE IF NOT EXISTS ""ActiveLocks"" (
            ""LockId""           TEXT NOT NULL CONSTRAINT ""PK_ActiveLocks"" PRIMARY KEY,
            ""FileKey""          TEXT NOT NULL,
            ""FileName""         TEXT NOT NULL,
            ""UserName""         TEXT NOT NULL,
            ""DisplayName""      TEXT,
            ""Email""            TEXT,
            ""MachineName""      TEXT NOT NULL,
            ""LocalPath""        TEXT,
            ""AppInstanceId""    TEXT NOT NULL,
            ""AcquiredAtUtc""    TEXT NOT NULL,
            ""LastHeartbeatUtc"" TEXT NOT NULL,
            ""Status""           TEXT NOT NULL DEFAULT 'Active',
            ""ReleasedReason""   TEXT,
            ""ReleasedAtUtc""    TEXT
        )");

    await ctx.Database.ExecuteSqlRawAsync(@"
        CREATE TABLE IF NOT EXISTS ""AuditLogs"" (
            ""AuditId""        INTEGER NOT NULL CONSTRAINT ""PK_AuditLogs"" PRIMARY KEY AUTOINCREMENT,
            ""TimestampUtc""   TEXT NOT NULL,
            ""EventType""      TEXT NOT NULL,
            ""FileKey""        TEXT,
            ""FileName""       TEXT,
            ""UserName""       TEXT,
            ""MachineName""    TEXT,
            ""AppInstanceId""  TEXT,
            ""LockId""         TEXT,
            ""Details""        TEXT
        )");

    await ctx.Database.ExecuteSqlRawAsync(@"
        CREATE UNIQUE INDEX IF NOT EXISTS ""UQ_ActiveLocks_FileKey_Active""
        ON ""ActiveLocks"" (""FileKey"") WHERE ""Status"" = 'Active'");

    await ctx.Database.ExecuteSqlRawAsync(@"
        CREATE INDEX IF NOT EXISTS ""IX_AuditLogs_TimestampUtc""
        ON ""AuditLogs"" (""TimestampUtc"")");

    await ctx.Database.ExecuteSqlRawAsync(@"
        CREATE INDEX IF NOT EXISTS ""IX_AuditLogs_FileKey""
        ON ""AuditLogs"" (""FileKey"")");

    await ctx.Database.ExecuteSqlRawAsync(@"
        CREATE TABLE IF NOT EXISTS ""ClientCommands"" (
            ""CommandId""               INTEGER NOT NULL CONSTRAINT ""PK_ClientCommands"" PRIMARY KEY AUTOINCREMENT,
            ""AppInstanceId""           TEXT NOT NULL,
            ""Command""                 TEXT NOT NULL,
            ""FileKey""                 TEXT,
            ""InitiatorAppInstanceId""  TEXT,
            ""CreatedAtUtc""            TEXT NOT NULL DEFAULT (datetime('now')),
            ""AcknowledgedAtUtc""       TEXT
        )");

    // Migration: add InitiatorAppInstanceId to existing ClientCommands tables that predate this column.
    try
    {
        await ctx.Database.ExecuteSqlRawAsync(
            @"ALTER TABLE ""ClientCommands"" ADD COLUMN ""InitiatorAppInstanceId"" TEXT");
    }
    catch { /* column already exists — safe to ignore */ }

    await ctx.Database.ExecuteSqlRawAsync(@"
        CREATE INDEX IF NOT EXISTS ""IX_ClientCommands_Pending""
        ON ""ClientCommands"" (""AppInstanceId"") WHERE ""AcknowledgedAtUtc"" IS NULL");

    logger.LogInformation("Database schema ready.");
}

// Middleware
app.UseMiddleware<ApiKeyMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

var serviceVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
var serviceVersionStr = serviceVersion is { } v
    ? (v.Build > 0 ? $"{v.Major}.{v.Minor}.{v.Build}" : $"{v.Major}.{v.Minor}")
    : "unknown";

app.MapGet("/health", () => Results.Ok(new { status = "healthy", version = serviceVersionStr, utc = DateTime.UtcNow }))
   .WithName("Health");

// GET /api/files/locks — all active locks (for displaying file status in the UI)
app.MapGet("/api/files/locks", async (ILockService svc) =>
{
    var locks = await svc.GetAllLocksAsync();
    return Results.Ok(locks);
})
.WithName("GetAllLocks")
.WithSummary("Returns all currently active locks.");

// GET /api/locks/mine?appInstanceId=xxx
app.MapGet("/api/locks/mine", async (string appInstanceId, ILockService svc) =>
{
    if (string.IsNullOrWhiteSpace(appInstanceId))
        return Results.BadRequest(new { error = "appInstanceId is required." });
    var locks = await svc.GetMyLocksAsync(appInstanceId);
    return Results.Ok(locks);
})
.WithName("GetMyLocks")
.WithSummary("Returns locks held by the specified app instance.");

// POST /api/locks/acquire
app.MapPost("/api/locks/acquire", async (AcquireLockRequest req, ILockService svc) =>
{
    if (string.IsNullOrWhiteSpace(req.FileKey) ||
        string.IsNullOrWhiteSpace(req.UserName) ||
        string.IsNullOrWhiteSpace(req.MachineName) ||
        string.IsNullOrWhiteSpace(req.AppInstanceId))
    {
        return Results.BadRequest(new { error = "FileKey, UserName, MachineName, and AppInstanceId are required." });
    }

    var result = await svc.AcquireAsync(req);
    return result.Status switch
    {
        "Acquired" => Results.Ok(result),
        "AlreadyOwned" => Results.Ok(result),
        "Denied" => Results.Conflict(result),
        _ => Results.StatusCode(500)
    };
})
.WithName("AcquireLock")
.WithSummary("Atomically acquires a lock on a QuickBooks company file.");

// POST /api/locks/heartbeat
app.MapPost("/api/locks/heartbeat", async (HeartbeatRequest req, ILockService svc) =>
{
    if (string.IsNullOrWhiteSpace(req.LockId) || string.IsNullOrWhiteSpace(req.AppInstanceId))
        return Results.BadRequest(new { error = "LockId and AppInstanceId are required." });

    var result = await svc.HeartbeatAsync(req);
    return result.Success ? Results.Ok(result) : Results.NotFound(result);
})
.WithName("Heartbeat")
.WithSummary("Updates the heartbeat timestamp for an active lock.");

// POST /api/locks/release
app.MapPost("/api/locks/release", async (ReleaseLockRequest req, ILockService svc) =>
{
    if (string.IsNullOrWhiteSpace(req.LockId) || string.IsNullOrWhiteSpace(req.AppInstanceId))
        return Results.BadRequest(new { error = "LockId and AppInstanceId are required." });

    var result = await svc.ReleaseAsync(req);
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
})
.WithName("ReleaseLock")
.WithSummary("Releases a lock owned by the caller.");

// POST /api/locks/force-release
app.MapPost("/api/locks/force-release", async (ForceReleaseRequest req, ILockService svc) =>
{
    if (string.IsNullOrWhiteSpace(req.LockId) || string.IsNullOrWhiteSpace(req.AdminUserName))
        return Results.BadRequest(new { error = "LockId and AdminUserName are required." });

    var result = await svc.ForceReleaseAsync(req);
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
})
.WithName("ForceReleaseLock")
.WithSummary("Admin-only: force-releases any active lock and writes an audit record.");

// POST /api/commands/poll — returns and acknowledges pending commands for this app instance
app.MapPost("/api/commands/poll", async (PollCommandsRequest req, ILockService svc) =>
{
    if (string.IsNullOrWhiteSpace(req.AppInstanceId))
        return Results.BadRequest(new { error = "AppInstanceId is required." });
    var commands = await svc.PollAndAcknowledgeCommandsAsync(req.AppInstanceId);
    return Results.Ok(commands);
})
.WithName("PollCommands")
.WithSummary("Returns and acknowledges pending commands for the given app instance.");

// GET /api/audit?limit=100&fileKey=optional
app.MapGet("/api/audit", async (ILockService svc, int limit = 100, string? fileKey = null) =>
{
    var entries = await svc.GetAuditLogAsync(limit, fileKey);
    return Results.Ok(entries);
})
.WithName("GetAuditLog")
.WithSummary("Returns recent audit log entries.");

app.Run();

// Expose Program for testing
public partial class Program { }
