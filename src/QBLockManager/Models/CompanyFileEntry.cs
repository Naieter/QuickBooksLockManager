namespace QBLockManager.Models;

public enum FileAvailability
{
    Available,
    LockedByMe,
    LockedByOther,
    Stale,
    NotFound
}

public class CompanyFileEntry
{
    public string FileKey { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string LocalPath { get; set; } = string.Empty;
    public bool ExistsLocally { get; set; }
    public long? FileSizeBytes { get; set; }
    public DateTime? LastModified { get; set; }

    // Populated from lock service
    public FileAvailability Availability { get; set; } = FileAvailability.Available;
    public LockInfoDto? CurrentLock { get; set; }
}

public class LockInfoDto
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
