using System.ComponentModel.DataAnnotations;

namespace QBLockService.Models;

public static class AuditEventType
{
    public const string LockAcquired = "LockAcquired";
    public const string LockDenied = "LockDenied";
    public const string LockAlreadyOwned = "LockAlreadyOwned";
    public const string LockReleased = "LockReleased";
    public const string HeartbeatReceived = "HeartbeatReceived";
    public const string HeartbeatMissed = "HeartbeatMissed";
    public const string LockMarkedStale = "LockMarkedStale";
    public const string StaleExpired = "StaleExpired";
    public const string ForceUnlock = "ForceUnlock";
    public const string BypassSuspected = "BypassSuspected";
    public const string ServiceUnavailable = "ServiceUnavailable";
}

public class AuditLog
{
    [Key]
    public long AuditId { get; set; }

    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    [Required, MaxLength(100)]
    public string EventType { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? FileKey { get; set; }

    [MaxLength(260)]
    public string? FileName { get; set; }

    [MaxLength(200)]
    public string? UserName { get; set; }

    [MaxLength(200)]
    public string? MachineName { get; set; }

    [MaxLength(100)]
    public string? AppInstanceId { get; set; }

    [MaxLength(100)]
    public string? LockId { get; set; }

    public string? Details { get; set; }
}
