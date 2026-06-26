using System.ComponentModel.DataAnnotations;

namespace QBLockService.Models;

public static class ClientCommandType
{
    public const string CloseQuickBooks = "CloseQuickBooks";
    public const string OpenFile        = "OpenFile";
}

public class ClientCommand
{
    [Key]
    public long CommandId { get; set; }

    [Required, MaxLength(100)]
    public string AppInstanceId { get; set; } = string.Empty;

    [Required, MaxLength(100)]
    public string Command { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? FileKey { get; set; }

    // For CloseQuickBooks: the AppInstanceId of the admin who triggered the force-release.
    // When the close is acknowledged, an OpenFile command is queued back to this instance.
    [MaxLength(100)]
    public string? InitiatorAppInstanceId { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? AcknowledgedAtUtc { get; set; }
}
