using System.IO;
using QBLockManager.Models;

namespace QBLockManager.Services;

public class FileScanner
{
    public List<CompanyFileEntry> Scan(AppSettings settings)
    {
        var results = new List<CompanyFileEntry>();

        foreach (var folder in settings.WatchedFolders)
        {
            if (string.IsNullOrWhiteSpace(folder.Path) || !Directory.Exists(folder.Path))
                continue;

            var searchOption = folder.Recursive
                ? SearchOption.AllDirectories
                : SearchOption.TopDirectoryOnly;

            try
            {
                var files = Directory.GetFiles(folder.Path, "*.QBW", searchOption);
                foreach (var filePath in files)
                {
                    var info = new FileInfo(filePath);
                    var fileKey = FileKeyService.ComputeFileKey(filePath, settings.SharedRootPath);

                    // Deduplicate by fileKey
                    if (results.Any(r => r.FileKey.Equals(fileKey, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    results.Add(new CompanyFileEntry
                    {
                        FileKey = fileKey,
                        FileName = info.Name,
                        LocalPath = filePath,
                        ExistsLocally = true,
                        FileSizeBytes = info.Exists ? info.Length : null,
                        LastModified = info.Exists ? info.LastWriteTimeUtc : null,
                        Availability = FileAvailability.Available
                    });
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (DirectoryNotFoundException) { }
            catch (IOException) { }  // Google Drive / network path errors
        }

        return results.OrderBy(f => f.FileName).ToList();
    }
}
