namespace QBLockManager.Models;

public class AppSettings
{
    // Set to true after the first-run wizard completes successfully.
    public bool SetupCompleted { get; set; } = false;

    public string LockServiceBaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string AdminApiKey { get; set; } = "";
    public bool IsAdminMode { get; set; } = false;

    public string QuickBooksExePath { get; set; } =
        @"C:\Program Files\Intuit\QuickBooks 2024\QBW.EXE";

    public bool MultiFileMode { get; set; } = false;

    // Optional: auto-filled into the QB login dialog when a company file is opened.
    public string? QuickBooksPassword { get; set; }

    public List<WatchedFolder> WatchedFolders { get; set; } = new();

    public string UserName { get; set; } = Environment.UserName;
    public string? DisplayName { get; set; }
    public string? Email { get; set; }

    public string SharedRootPath { get; set; } = "";

    public int HeartbeatIntervalSeconds { get; set; } = 20;
    public int ServiceTimeoutSeconds { get; set; } = 10;
}

public class WatchedFolder
{
    public string Path { get; set; } = "";
    public bool Recursive { get; set; } = false;
    public string? Label { get; set; }
}
