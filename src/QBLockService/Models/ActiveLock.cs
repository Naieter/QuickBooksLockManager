using System.ComponentModel.DataAnnotations;

namespace QBLockService.Models;

public enum LockStatus
{
    Active,
    Released,
    Expired,
    ForceReleased
}

public class ActiveLock
{
    [Key]
    public string LockId { get; set; } = Guid.NewGuid().ToString();

    [Required, MaxLength(500)]
    public string FileKey { get; set; } = string.Empty;

    [Required, MaxLength(260)]
    public string FileName { get; set; } = string.Empty;

    [Required, MaxLength(200)]
    public string UserName { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? DisplayName { get; set; }

    [MaxLength(200)]
    public string? Email { get; set; }

    [Required, MaxLength(200)]
    public string MachineName { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? LocalPath { get; set; }

    [Required, MaxLength(100)]
    public string AppInstanceId { get; set; } = string.Empty;

    public DateTime AcquiredAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastHeartbeatUtc { get; set; } = DateTime.UtcNow;

    public LockStatus Status { get; set; } = LockStatus.Active;

    [MaxLength(500)]
    public string? ReleasedReason { get; set; }

    public DateTime? ReleasedAtUtc { get; set; }
}
