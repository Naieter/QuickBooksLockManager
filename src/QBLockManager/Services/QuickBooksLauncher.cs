using System.Diagnostics;
using System.IO;

namespace QBLockManager.Services;

public class LaunchResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public Process? Process { get; init; }
    public bool ReusedExistingProcess { get; init; }
}

public class QuickBooksLauncher
{
    private static readonly string[] KnownQBPaths =
    [
        // QB 2024 and later use QBW.EXE
        @"C:\Program Files\Intuit\QuickBooks 2024\QBW.EXE",
        @"C:\Program Files (x86)\Intuit\QuickBooks 2024\QBW.EXE",
        // Enterprise Solutions (older) use QBW32.EXE
        @"C:\Program Files\Intuit\QuickBooks Enterprise Solutions 24.0\QBW32.EXE",
        @"C:\Program Files\Intuit\QuickBooks Enterprise Solutions 23.0\QBW32.EXE",
        @"C:\Program Files\Intuit\QuickBooks Enterprise Solutions 22.0\QBW32.EXE",
        @"C:\Program Files (x86)\Intuit\QuickBooks Enterprise Solutions 24.0\QBW32.EXE",
        @"C:\Program Files (x86)\Intuit\QuickBooks Enterprise Solutions 23.0\QBW32.EXE",
    ];

    public static bool IsQuickBooksRunning()
        => Process.GetProcessesByName("QBW32").Length > 0 ||
           Process.GetProcessesByName("QBW").Length > 0;

    public static string? FindQuickBooksExe()
    {
        foreach (var path in KnownQBPaths)
        {
            if (File.Exists(path)) return path;
        }
        return null;
    }

    public static LaunchResult Launch(string qbExePath, string companyFilePath)
    {
        if (!File.Exists(qbExePath))
        {
            return new LaunchResult
            {
                Success = false,
                Message = $"QuickBooks could not be found at: {qbExePath}. Please configure the QuickBooks executable path."
            };
        }

        if (!File.Exists(companyFilePath))
        {
            return new LaunchResult
            {
                Success = false,
                Message = $"This file is not currently available on this workstation: {companyFilePath}"
            };
        }

        bool wasAlreadyRunning = IsQuickBooksRunning();

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = qbExePath,
                Arguments = $"\"{companyFilePath}\"",
                UseShellExecute = true
            };

            var process = Process.Start(psi);

            return new LaunchResult
            {
                Success = true,
                Message = wasAlreadyRunning
                    ? "QuickBooks was already running. The file has been requested to open. Use the Lock Manager to manage locks — do NOT open additional company files from within QuickBooks."
                    : "QuickBooks launched.",
                Process = process,
                ReusedExistingProcess = wasAlreadyRunning
            };
        }
        catch (Exception ex)
        {
            return new LaunchResult
            {
                Success = false,
                Message = $"Failed to launch QuickBooks: {ex.Message}"
            };
        }
    }

    // Gracefully close all QB processes; force-kill after 10 s if they don't exit.
    public static async Task CloseQuickBooksAsync()
    {
        var procs = Process.GetProcessesByName("QBW32")
            .Concat(Process.GetProcessesByName("QBW"))
            .ToArray();

        foreach (var p in procs)
        {
            try { p.CloseMainWindow(); } catch { }
        }

        // Wait up to 10 s for graceful shutdown
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(500);
            if (!IsQuickBooksRunning()) return;
        }

        // Force-kill whatever is still alive
        foreach (var p in procs)
        {
            try { if (!p.HasExited) p.Kill(); } catch { }
        }

        await Task.Delay(500);
    }
}
