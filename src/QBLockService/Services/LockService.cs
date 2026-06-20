using Microsoft.EntityFrameworkCore;
using QBLockService.Data;
using QBLockService.Models;

namespace QBLockService.Services;

public interface ILockService
{
    Task<LockAcquireResult> AcquireAsync(AcquireLockRequest request);
    Task<HeartbeatResult> HeartbeatAsync(HeartbeatRequest request);
    Task<ReleaseResult> ReleaseAsync(ReleaseLockRequest request);
    Task<ReleaseResult> ForceReleaseAsync(ForceReleaseRequest request);
    Task<List<LockInfo>> GetAllLocksAsync();
    Task<List<LockInfo>> GetMyLocksAsync(string appInstanceId);
    Task<List<AuditLogEntry>> GetAuditLogAsync(int limit, string? fileKey);
    Task ExpireStaleLocksAsync();
}

public class LockService : ILockService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly IConfiguration _config;
    private readonly ILogger<LockService> _logger;

    public LockService(
        IDbContextFactory<AppDbContext> factory,
        IConfiguration config,
        ILogger<LockService> logger)
    {
        _factory = factory;
        _config = config;
        _logger = logger;
    }

    private int LockTimeoutMinutes =>
        _config.GetValue<int>("LockSettings:TimeoutMinutes", 5);

    public async Task<LockAcquireResult> AcquireAsync(AcquireLockRequest req)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        try
        {
            var existing = await db.ActiveLocks
                .Where(l => l.FileKey == req.FileKey && l.Status == LockStatus.Active)
                .FirstOrDefaultAsync();

            if (existing != null)
            {
                var cutoff = DateTime.UtcNow.AddMinutes(-LockTimeoutMinutes);
                bool isStale = existing.LastHeartbeatUtc < cutoff;

                if (!isStale)
                {
                    // Same session re-acquiring (idempotent)
                    if (existing.AppInstanceId == req.AppInstanceId)
                    {
                        await tx.CommitAsync();
                        await WriteAuditAsync(db, AuditEventType.LockAlreadyOwned, req, existing.LockId,
                            $"Re-acquire by same session. Returning existing lock.");

                        return new LockAcquireResult
                        {
                            Success = true,
                            Status = "AlreadyOwned",
                            Message = "You already have this file locked.",
                            Lock = ToLockInfo(existing, false)
                        };
                    }

                    // Held by another user/session
                    await WriteAuditAsync(db, AuditEventType.LockDenied, req, existing.LockId,
                        $"Denied. Held by {existing.UserName} on {existing.MachineName}.");
                    await tx.CommitAsync();

                    return new LockAcquireResult
                    {
                        Success = false,
                        Status = "Denied",
                        Message = $"This QuickBooks file is currently in use by {existing.UserName} on {existing.MachineName} since {existing.AcquiredAtUtc:u}.",
                        CurrentHolder = ToLockInfo(existing, false)
                    };
                }

                // Stale lock — expire it and allow new acquisition
                _logger.LogWarning("Expiring stale lock {LockId} for {FileKey} held by {User}",
                    existing.LockId, existing.FileKey, existing.UserName);

                existing.Status = LockStatus.Expired;
                existing.ReleasedAtUtc = DateTime.UtcNow;
                existing.ReleasedReason = $"Stale: heartbeat stopped {(DateTime.UtcNow - existing.LastHeartbeatUtc).TotalMinutes:F1} minutes ago.";

                db.AuditLogs.Add(new AuditLog
                {
                    EventType = AuditEventType.LockMarkedStale,
                    FileKey = existing.FileKey,
                    FileName = existing.FileName,
                    UserName = existing.UserName,
                    MachineName = existing.MachineName,
                    AppInstanceId = existing.AppInstanceId,
                    LockId = existing.LockId,
                    Details = existing.ReleasedReason
                });
            }

            var newLock = new ActiveLock
            {
                LockId = Guid.NewGuid().ToString(),
                FileKey = req.FileKey,
                FileName = req.FileName,
                UserName = req.UserName,
                DisplayName = req.DisplayName,
                Email = req.Email,
                MachineName = req.MachineName,
                LocalPath = req.LocalPath,
                AppInstanceId = req.AppInstanceId,
                AcquiredAtUtc = DateTime.UtcNow,
                LastHeartbeatUtc = DateTime.UtcNow,
                Status = LockStatus.Active
            };

            db.ActiveLocks.Add(newLock);

            db.AuditLogs.Add(new AuditLog
            {
                EventType = AuditEventType.LockAcquired,
                FileKey = req.FileKey,
                FileName = req.FileName,
                UserName = req.UserName,
                MachineName = req.MachineName,
                AppInstanceId = req.AppInstanceId,
                LockId = newLock.LockId,
                Details = $"Lock acquired. Path: {req.LocalPath}"
            });

            try
            {
                await db.SaveChangesAsync();
                await tx.CommitAsync();
            }
            catch (DbUpdateException ex) when (IsDuplicateKey(ex))
            {
                // Race condition: another request acquired between our read and insert
                await tx.RollbackAsync();

                await using var db2 = await _factory.CreateDbContextAsync();
                var current = await db2.ActiveLocks
                    .FirstOrDefaultAsync(l => l.FileKey == req.FileKey && l.Status == LockStatus.Active);

                return new LockAcquireResult
                {
                    Success = false,
                    Status = "Denied",
                    Message = current != null
                        ? $"This QuickBooks file is currently in use by {current.UserName} on {current.MachineName} since {current.AcquiredAtUtc:u}."
                        : "File lock acquisition failed due to a concurrent request.",
                    CurrentHolder = current != null ? ToLockInfo(current, false) : null
                };
            }

            _logger.LogInformation("Lock acquired: {LockId} for {FileKey} by {User}",
                newLock.LockId, req.FileKey, req.UserName);

            return new LockAcquireResult
            {
                Success = true,
                Status = "Acquired",
                Message = "Lock acquired.",
                Lock = ToLockInfo(newLock, false)
            };
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    public async Task<HeartbeatResult> HeartbeatAsync(HeartbeatRequest req)
    {
        await using var db = await _factory.CreateDbContextAsync();

        var lock_ = await db.ActiveLocks
            .FirstOrDefaultAsync(l => l.LockId == req.LockId && l.Status == LockStatus.Active);

        if (lock_ == null)
        {
            return new HeartbeatResult { Success = false, Message = "Lock not found or no longer active." };
        }

        if (lock_.AppInstanceId != req.AppInstanceId)
        {
            return new HeartbeatResult { Success = false, Message = "AppInstanceId mismatch." };
        }

        lock_.LastHeartbeatUtc = DateTime.UtcNow;

        db.AuditLogs.Add(new AuditLog
        {
            EventType = AuditEventType.HeartbeatReceived,
            FileKey = lock_.FileKey,
            FileName = lock_.FileName,
            UserName = lock_.UserName,
            MachineName = lock_.MachineName,
            AppInstanceId = lock_.AppInstanceId,
            LockId = lock_.LockId
        });

        await db.SaveChangesAsync();
        return new HeartbeatResult { Success = true, Message = "Heartbeat recorded." };
    }

    public async Task<ReleaseResult> ReleaseAsync(ReleaseLockRequest req)
    {
        await using var db = await _factory.CreateDbContextAsync();

        var lock_ = await db.ActiveLocks
            .FirstOrDefaultAsync(l => l.LockId == req.LockId && l.Status == LockStatus.Active);

        if (lock_ == null)
        {
            return new ReleaseResult { Success = false, Message = "Lock not found or already released." };
        }

        if (lock_.AppInstanceId != req.AppInstanceId)
        {
            return new ReleaseResult { Success = false, Message = "AppInstanceId mismatch. Cannot release a lock you do not own." };
        }

        lock_.Status = LockStatus.Released;
        lock_.ReleasedAtUtc = DateTime.UtcNow;
        lock_.ReleasedReason = "Normal release.";

        db.AuditLogs.Add(new AuditLog
        {
            EventType = AuditEventType.LockReleased,
            FileKey = lock_.FileKey,
            FileName = lock_.FileName,
            UserName = lock_.UserName,
            MachineName = lock_.MachineName,
            AppInstanceId = lock_.AppInstanceId,
            LockId = lock_.LockId,
            Details = "Normal release."
        });

        await db.SaveChangesAsync();
        _logger.LogInformation("Lock released: {LockId} for {FileKey}", req.LockId, lock_.FileKey);
        return new ReleaseResult { Success = true, Message = "Lock released." };
    }

    public async Task<ReleaseResult> ForceReleaseAsync(ForceReleaseRequest req)
    {
        await using var db = await _factory.CreateDbContextAsync();

        var lock_ = await db.ActiveLocks
            .FirstOrDefaultAsync(l => l.LockId == req.LockId && l.Status == LockStatus.Active);

        if (lock_ == null)
        {
            return new ReleaseResult { Success = false, Message = "Lock not found or already released." };
        }

        lock_.Status = LockStatus.ForceReleased;
        lock_.ReleasedAtUtc = DateTime.UtcNow;
        lock_.ReleasedReason = $"Force-released by {req.AdminUserName}. Reason: {req.Reason}";

        db.AuditLogs.Add(new AuditLog
        {
            EventType = AuditEventType.ForceUnlock,
            FileKey = lock_.FileKey,
            FileName = lock_.FileName,
            UserName = lock_.UserName,
            MachineName = lock_.MachineName,
            AppInstanceId = lock_.AppInstanceId,
            LockId = lock_.LockId,
            Details = $"Force-released by admin: {req.AdminUserName}. Reason: {req.Reason}. WARNING: The original user may still have the file open."
        });

        await db.SaveChangesAsync();
        _logger.LogWarning("FORCE UNLOCK by {Admin}: LockId={LockId} FileKey={FileKey} OriginalUser={User}",
            req.AdminUserName, req.LockId, lock_.FileKey, lock_.UserName);
        return new ReleaseResult { Success = true, Message = $"Lock force-released. WARNING: {lock_.UserName} on {lock_.MachineName} may still have the file open." };
    }

    public async Task<List<LockInfo>> GetAllLocksAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var cutoff = DateTime.UtcNow.AddMinutes(-LockTimeoutMinutes);

        var locks = await db.ActiveLocks
            .Where(l => l.Status == LockStatus.Active)
            .OrderBy(l => l.FileKey)
            .ToListAsync();

        return locks.Select(l => ToLockInfo(l, l.LastHeartbeatUtc < cutoff)).ToList();
    }

    public async Task<List<LockInfo>> GetMyLocksAsync(string appInstanceId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var cutoff = DateTime.UtcNow.AddMinutes(-LockTimeoutMinutes);

        var locks = await db.ActiveLocks
            .Where(l => l.AppInstanceId == appInstanceId && l.Status == LockStatus.Active)
            .OrderBy(l => l.FileKey)
            .ToListAsync();

        return locks.Select(l => ToLockInfo(l, l.LastHeartbeatUtc < cutoff)).ToList();
    }

    public async Task<List<AuditLogEntry>> GetAuditLogAsync(int limit, string? fileKey)
    {
        await using var db = await _factory.CreateDbContextAsync();

        var query = db.AuditLogs.AsQueryable();
        if (!string.IsNullOrEmpty(fileKey))
            query = query.Where(a => a.FileKey == fileKey);

        var entries = await query
            .OrderByDescending(a => a.TimestampUtc)
            .Take(Math.Min(limit, 1000))
            .ToListAsync();

        return entries.Select(e => new AuditLogEntry
        {
            AuditId = e.AuditId,
            TimestampUtc = e.TimestampUtc,
            EventType = e.EventType,
            FileKey = e.FileKey,
            FileName = e.FileName,
            UserName = e.UserName,
            MachineName = e.MachineName,
            AppInstanceId = e.AppInstanceId,
            LockId = e.LockId,
            Details = e.Details
        }).ToList();
    }

    public async Task ExpireStaleLocksAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var cutoff = DateTime.UtcNow.AddMinutes(-LockTimeoutMinutes);

        var stale = await db.ActiveLocks
            .Where(l => l.Status == LockStatus.Active && l.LastHeartbeatUtc < cutoff)
            .ToListAsync();

        foreach (var l in stale)
        {
            _logger.LogWarning("Expiring stale lock {LockId} for {FileKey}. Last heartbeat: {Hb}",
                l.LockId, l.FileKey, l.LastHeartbeatUtc);

            l.Status = LockStatus.Expired;
            l.ReleasedAtUtc = DateTime.UtcNow;
            l.ReleasedReason = $"Auto-expired: no heartbeat since {l.LastHeartbeatUtc:u}.";

            db.AuditLogs.Add(new AuditLog
            {
                EventType = AuditEventType.StaleExpired,
                FileKey = l.FileKey,
                FileName = l.FileName,
                UserName = l.UserName,
                MachineName = l.MachineName,
                AppInstanceId = l.AppInstanceId,
                LockId = l.LockId,
                Details = l.ReleasedReason
            });
        }

        if (stale.Count > 0)
            await db.SaveChangesAsync();
    }

    private static bool IsDuplicateKey(DbUpdateException ex)
    {
        var msg = ex.InnerException?.Message ?? ex.Message;
        return msg.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("unique constraint", StringComparison.OrdinalIgnoreCase);
    }

    private static LockInfo ToLockInfo(ActiveLock l, bool isStale) => new()
    {
        LockId = l.LockId,
        FileKey = l.FileKey,
        FileName = l.FileName,
        UserName = l.UserName,
        DisplayName = l.DisplayName,
        MachineName = l.MachineName,
        LocalPath = l.LocalPath,
        AppInstanceId = l.AppInstanceId,
        AcquiredAtUtc = l.AcquiredAtUtc,
        LastHeartbeatUtc = l.LastHeartbeatUtc,
        Status = l.Status.ToString(),
        IsStale = isStale
    };

    private static async Task WriteAuditAsync(AppDbContext db, string eventType, AcquireLockRequest req, string? lockId, string? details)
    {
        db.AuditLogs.Add(new AuditLog
        {
            EventType = eventType,
            FileKey = req.FileKey,
            FileName = req.FileName,
            UserName = req.UserName,
            MachineName = req.MachineName,
            AppInstanceId = req.AppInstanceId,
            LockId = lockId,
            Details = details
        });
        await db.SaveChangesAsync();
    }
}
