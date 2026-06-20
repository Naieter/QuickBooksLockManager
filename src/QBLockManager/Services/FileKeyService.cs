using System.IO;
using QBLockManager.Models;

namespace QBLockManager.Services;

/// <summary>
/// Computes a stable, machine-agnostic file key for a .QBW file.
/// Strategy: normalize the path relative to a configured shared root.
/// Falls back to normalized full path if no shared root is configured.
/// </summary>
public static class FileKeyService
{
    public static string ComputeFileKey(string localPath, string sharedRootPath)
    {
        var normalized = NormalizePath(localPath);

        if (!string.IsNullOrWhiteSpace(sharedRootPath))
        {
            var normalizedRoot = NormalizePath(sharedRootPath);
            if (normalized.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                var relative = normalized[normalizedRoot.Length..].TrimStart('/');
                return relative.ToLowerInvariant();
            }
        }

        // Fall back: use just the filename portion with a hash of the normalized path
        // to distinguish files with the same name in different folders
        var fileName = Path.GetFileName(normalized).ToLowerInvariant();
        var hash = Math.Abs(normalized.ToLowerInvariant().GetHashCode()).ToString("x8");
        return $"{fileName}_{hash}";
    }

    private static string NormalizePath(string path)
        => path.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
}
