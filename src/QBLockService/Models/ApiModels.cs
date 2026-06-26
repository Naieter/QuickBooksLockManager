using System.ComponentModel.DataAnnotations;

namespace QBLockService.Models;

public class AcquireLockRequest
{
    [Required, MaxLength(500)]
    public string FileKey { get; set; } = string.Empty;

    [Required, MaxLength(260)]
    public string FileName { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? LocalPath { get; set; }

    [Required, MaxLength(200)]
    public string UserName { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? DisplayName { get; set; }

    [MaxLength(200)]
    public string? Email { get; set; }

    [Required, MaxLength(200)]
    public string MachineName { get; set; } = string.Empty;

    [Required, MaxLength(100)]
    public string AppInstanceId { get; set; } = string.Empty;
}

public class HeartbeatRequest
{
    [Required, MaxLength(100)]
    public string LockId { get; set; } = string.Empty;

    [Required, MaxLength(100)]
    public string AppInstanceId { get; set; } = string.Empty;

    // Set only when the QB company file's LastWriteTime has changed since the previous heartbeat.
    public DateTime? FileModifiedAtUtc { get; set; }
}

public class ReleaseLockRequest
{
    [Required, MaxLength(100)]
    public string LockId { get; set; } = string.Empty;

    [Required, MaxLength(100)]
    public string AppInstanceId { get; set; } = string.Empty;
}

public class ForceReleaseRequest
{
    [Required, MaxLength(100)]
    public string LockId { get; set; } = string.Empty;

    [Required, MaxLength(200)]
    public string AdminUserName { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Reason { get; set; }

    // When set, an OpenFile command is queued back to this instance after the target closes QB.
    [MaxLength(100)]
    public string? AdminAppInstanceId { get; set; }
}

public class LockAcquireResult
{
    public bool Success { get; set; }
    public string Status { get; set; } = string.Empty; // Acquired, Denied, AlreadyOwned, Error
    public string? Message { get; set; }
    public LockInfo? Lock { get; set; }
    public LockInfo? CurrentHolder { get; set; }
}

public class LockInfo
{
    public string LockId { get; set; } = string.Empty;
    public string FileKey { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string MachineName { get; set; } = string.Empty;
    public string? LocalPath { get; set; }
    public string AppInstanceId { get; set; } = string.Empty;
    public DateTime AcquiredAtUtc { get; set; }
    public DateTime LastHeartbeatUtc { get; set; }
    public string Status { get; set; } = string.Empty;
    public bool IsStale { get; set; }
}

public class HeartbeatResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class ReleaseResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class PollCommandsRequest
{
    [Required, MaxLength(100)]
    public string AppInstanceId { get; set; } = string.Empty;
}

public class PendingCommandDto
{
    public long CommandId { get; set; }
    public string Command { get; set; } = string.Empty;
    public string? FileKey { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public class AuditLogEntry
{
    public long AuditId { get; set; }
    public DateTime TimestampUtc { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string? FileKey { get; set; }
    public string? FileName { get; set; }
    public string? UserName { get; set; }
    public string? MachineName { get; set; }
    public string? AppInstanceId { get; set; }
    public string? LockId { get; set; }
    public string? Details { get; set; }
}
