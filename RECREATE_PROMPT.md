# Claude Code Prompt — Recreate QuickBooks Lock Manager

Build a Windows desktop application called **QuickBooks Lock Manager** that coordinates multi-user access to QuickBooks company files across a network. When one user opens a QB company file, it places a distributed lock so other users see it as "In Use" and are blocked from opening it simultaneously.

---

## Solution Structure

Three C# projects in `src/`:

```
src/
  QBLockService/          ← ASP.NET Core 8 Minimal API, runs as a Windows Service
  QBLockManager/          ← WPF .NET 8 desktop client (workstation app)
  QBLockServiceSetup/     ← WPF .NET 8 one-click installer for QBLockService
```

---

## Project 1: QBLockService

**Purpose:** Central lock server. Runs on one machine as a Windows Service. Exposes a REST API over HTTP (default port 5100). All workstations connect to it.

**Tech:** ASP.NET Core 8 Minimal API, Entity Framework Core with SQLite, `UseWindowsService()`.

### Database schema (SQLite, created with raw SQL on startup — do NOT use EF migrations, they are unreliable when running as a Windows Service)

**ActiveLocks table:**
- `LockId` TEXT PK
- `FileKey` TEXT (normalized file identifier, e.g. SHA256 of the canonical file path)
- `FileName` TEXT (display name)
- `UserName` TEXT
- `DisplayName` TEXT (nullable)
- `Email` TEXT (nullable)
- `MachineName` TEXT
- `LocalPath` TEXT (nullable)
- `AppInstanceId` TEXT (GUID generated per app launch, identifies the specific workstation session)
- `AcquiredAtUtc` TEXT
- `LastHeartbeatUtc` TEXT
- `Status` TEXT DEFAULT 'Active' — values: Active, Released, Expired, ForceReleased
- `ReleasedReason` TEXT (nullable)
- `ReleasedAtUtc` TEXT (nullable)

**AuditLogs table:**
- `AuditId` INTEGER PK AUTOINCREMENT
- `TimestampUtc` TEXT DEFAULT current UTC
- `EventType` TEXT — values: LockAcquired, LockReleased, LockDenied, LockAlreadyOwned, LockMarkedStale, StaleExpired, HeartbeatReceived, ForceUnlock
- `FileKey`, `FileName`, `UserName`, `MachineName`, `AppInstanceId`, `LockId`, `Details` — all TEXT nullable

**Indexes:**
- `UNIQUE INDEX ON ActiveLocks(FileKey) WHERE Status = 'Active'` — enforces one active lock per file
- `INDEX ON AuditLogs(TimestampUtc)`
- `INDEX ON AuditLogs(FileKey)`

On startup, call `Directory.SetCurrentDirectory(AppContext.BaseDirectory)` BEFORE creating the DB so relative paths resolve correctly under the service CWD.

### API endpoints (all require `X-Api-Key` header except `/health`)

- `GET /health` — returns `{ status, utc }`
- `GET /api/files/locks` — returns all active locks
- `GET /api/locks/mine?appInstanceId=xxx` — returns locks held by this session
- `POST /api/locks/acquire` — acquires a lock; handles race conditions with a unique index + retry on `DbUpdateException`; auto-expires stale locks (heartbeat older than `LockSettings:TimeoutMinutes`, default 5) on acquire
- `POST /api/locks/heartbeat` — updates `LastHeartbeatUtc` for an active lock
- `POST /api/locks/release` — releases a lock owned by the caller
- `POST /api/locks/force-release` — admin only, requires `X-Admin-Key` header; force-releases any lock and logs a warning

### Acquire logic (critical)
1. Check for existing Active lock on `FileKey`.
2. If found and NOT stale: if same `AppInstanceId` → return AlreadyOwned; else → return Denied.
3. If found and stale: mark Expired, log it, continue to step 4.
4. Insert new lock. Catch UNIQUE constraint violation (race condition) → return Denied with current holder info.

### StaleChecker background service
`IHostedService` that calls `ExpireStaleLocksAsync()` every 60 seconds to mark locks with no heartbeat as Expired.

### API key middleware
- `ApiKeyMiddleware`: checks `X-Api-Key` header against `ApiKeys` config array. Passes `/health` through unauthenticated.
- `AdminApiKeyMiddleware`: checks `X-Admin-Key` header against `AdminApiKeys` config array. Only enforced on `/api/locks/force-release`.

### appsettings.json (runtime config, NOT committed to git)
```json
{
  "Urls": "http://0.0.0.0:5100",
  "Database": { "Path": "data/qblocks.db" },
  "LockSettings": { "TimeoutMinutes": 5 },
  "ApiKeys": ["<key1>"],
  "AdminApiKeys": ["<adminkey1>"],
  "Logging": { "LogLevel": { "Default": "Information" } }
}
```

### csproj
```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <RootNamespace>QBLockService</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="8.*" />
    <PackageReference Include="Microsoft.Extensions.Hosting.WindowsServices" Version="8.*" />
    <PackageReference Include="Swashbuckle.AspNetCore" Version="6.*" />
  </ItemGroup>
</Project>
```

---

## Project 2: QBLockManager (WPF client)

**Purpose:** Workstation app. Shows a list of QB company files found in configured folders. Lets the user acquire a lock and open the file in QuickBooks. Releases the lock when QB closes or the user exits.

**Tech:** WPF .NET 8, `net8.0-windows`, CommunityToolkit.Mvvm, System.Text.Json.

### Self-install behavior (App.xaml.cs)
On startup, if running from OUTSIDE `%LOCALAPPDATA%\Programs\QBLockManager\`, prompt to install. If yes:
1. `Directory.CreateDirectory(installDir)`
2. `File.Copy(currentExe, installedExe, overwrite: true)`
3. Create Start Menu and Desktop shortcuts via `WScript.Shell` COM object
4. `Process.Start(installedExe)` then `Application.Current.Shutdown()`

If the installed exe is already byte-for-byte identical (same length + write time), skip the install prompt and run in-place.

### Settings (stored at `%APPDATA%\QBLockManager\appsettings.json`)
```csharp
public class AppSettings
{
    public bool SetupCompleted { get; set; }
    public string LockServiceBaseUrl { get; set; }  // e.g. "http://192.168.50.105:5100"
    public string ApiKey { get; set; }
    public string AdminApiKey { get; set; }
    public bool IsAdminMode { get; set; }
    public string QuickBooksExePath { get; set; }   // path to QBW.EXE or QBW32.EXE
    public string? QuickBooksPassword { get; set; } // auto-filled into QB login dialog
    public bool MultiFileMode { get; set; }
    public List<WatchedFolder> WatchedFolders { get; set; }
    public string UserName { get; set; }
    public string? DisplayName { get; set; }
    public string? Email { get; set; }
    public string SharedRootPath { get; set; }
    public int HeartbeatIntervalSeconds { get; set; } = 20;
    public int ServiceTimeoutSeconds { get; set; } = 10;
}

public class WatchedFolder { public string Path; public bool Recursive; public string? Label; }
```

### First-run wizard (SetupWizardWindow)
Multi-step WPF dialog that collects: server URL, API key, QB exe path, watched folders, username, and optionally QB password. Tests connectivity before completing. Sets `SetupCompleted = true` and saves.

### File discovery (FileScanner)
Scans `WatchedFolders` for `*.QBW` files. For each file:
- `FileKey` = SHA256 of the lowercase canonical path relative to `SharedRootPath` (so the same file has the same key from any workstation, even if drive letters differ)
- `FileName` = display name (file name without extension)
- `LocalPath` = full local path
- `ExistsLocally` = true

### FileKey generation (critical for cross-machine consistency)
```csharp
// Normalize: lowercase, forward slashes, strip trailing slash
// If SharedRootPath is configured, use path relative to it.
// Hash with SHA256, return first 16 hex chars.
string key = Convert.ToHexString(
    SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)))[..16].ToLowerInvariant();
```

### LockServiceClient
`HttpClient` wrapper that calls all the API endpoints. Sets `X-Api-Key` and `X-Admin-Key` headers. Has `IsReachableAsync()` that hits `/health`. All methods return typed DTOs.

### HeartbeatManager
Background service (Timer, not IHostedService) that:
- Maintains a `ConcurrentDictionary<string, ActiveFileLock>` of currently held locks
- Sends `POST /api/locks/heartbeat` for each lock every `HeartbeatIntervalSeconds` (default 20)
- Fires `LockLost` event if heartbeat fails 3 times in a row
- `RegisterLock(lock)` / `UnregisterLock(lockId)` / `ReleaseAllAsync()`

### MainViewModel (MVVM with CommunityToolkit.Mvvm)
`ObservableObject` with `RelayCommand` attributes. Key commands:
- `RefreshAsync` — polls `/api/files/locks`, overlays availability on scanned files
- `OpenFileAsync` — acquires lock → launches QB → begins auto-login
- `SwitchFileAsync` — releases existing locks + closes QB + opens new file
- `ReleaseSelectedLockAsync` — releases one lock
- `AdminForceUnlockAsync` — calls force-release (admin only)
- `ShowAuditLogAsync` — opens AuditLogWindow
- `OpenSettings` — opens SettingsWindow

`CheckQuickBooksAsync` timer (every 5s): if QB process is not running but we hold locks, release them all automatically.

### File status colors (via converter)
- Available → Green
- LockedByMe → Blue  
- LockedByOther → Red
- Stale → Orange
- NotFound → Gray

### QuickBooksLauncher
```csharp
Process.Start(new ProcessStartInfo {
    FileName = qbExePath,
    Arguments = $"\"{companyFilePath}\"",
    UseShellExecute = true
});
```
Also has `IsQuickBooksRunning()` (checks for `QBW32` or `QBW` process names) and `CloseQuickBooksAsync()` (graceful close with 10s force-kill fallback).

### QuickBooksAutoLogin (CRITICAL — read every word)

This is the most complex part. QB uses **Intuit's proprietary Maui UI framework**, NOT standard Win32 dialogs. This means:
- The login dialog window class is `MauiForm`, NOT `#32770`
- `WM_COMMAND` with `IDOK` is silently **ignored** — do not use it
- `GetDlgItem` does **not** work on Maui controls
- `FindWindow` returns 0 because the dialog is a **child window** of the main QB window, not top-level

The correct approach:

```csharp
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

public static class QuickBooksAutoLogin
{
    // Only records dialogs that were SUCCESSFULLY handled.
    // Failed attempts are NOT recorded — the watcher retries them every 500ms.
    private static readonly HashSet<IntPtr> _succeeded = new();
    private static readonly object _lock = new();

    public static void BeginAutoLogin(string? password)
    {
        if (string.IsNullOrWhiteSpace(password)) return;
        Task.Run(() => RunWatcher(password));
    }

    public static void ResetAttempts()
    {
        lock (_lock) _succeeded.Clear();
    }

    private static void RunWatcher(string password)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            try { if (TryLoginAllWindows(password)) return; }
            catch { }
            Thread.Sleep(500);
        }
    }

    private static bool TryLoginAllWindows(string password)
    {
        var procs = Process.GetProcessesByName("QBW32")
            .Concat(Process.GetProcessesByName("QBW")).ToArray();
        if (procs.Length == 0) return false;
        foreach (var proc in procs)
        {
            try
            {
                var windows = AutomationElement.RootElement.FindAll(
                    TreeScope.Children,
                    new PropertyCondition(AutomationElement.ProcessIdProperty, proc.Id));
                foreach (AutomationElement win in windows)
                {
                    try { if (TryLoginInWindow(win, password)) return true; }
                    catch { }
                }
            }
            catch { }
        }
        return false;
    }

    private static bool TryLoginInWindow(AutomationElement window, string password)
    {
        AutomationElementCollection edits;
        try
        {
            edits = window.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
        }
        catch { return false; }

        AutomationElement? target = null;
        foreach (AutomationElement e in edits)
        {
            try { if (e.Current.IsPassword && target == null) target = e; }
            catch { }
        }
        if (target == null && edits.Count == 1) target = edits[0];
        if (target == null) return false;

        IntPtr editHwnd = IntPtr.Zero;
        try { editHwnd = new IntPtr(target.Current.NativeWindowHandle); }
        catch { }

        // dialogHwnd = the MauiForm login dialog (parent of the edit control)
        IntPtr dialogHwnd = editHwnd != IntPtr.Zero ? GetParent(editHwnd) : IntPtr.Zero;
        if (dialogHwnd == IntPtr.Zero) return false;

        lock (_lock) { if (_succeeded.Contains(dialogHwnd)) return false; }

        // Fill password — try UI Automation ValuePattern first, fall back to WM_SETTEXT
        bool filled = false;
        try
        {
            if (target.TryGetCurrentPattern(ValuePattern.Pattern, out var vp))
            {
                ((ValuePattern)vp).SetValue(password);
                filled = true;
            }
        }
        catch { }
        if (!filled && editHwnd != IntPtr.Zero)
        {
            try { SendMessage(editHwnd, WM_SETTEXT, IntPtr.Zero, password); filled = true; }
            catch { }
        }
        if (!filled) return false;

        Thread.Sleep(150);

        // Submit via SendInput Enter.
        // AttachThreadInput is required because Windows blocks cross-process SetForegroundWindow
        // and SetFocus without it — the Maui form will not receive the keystroke.
        bool submitted = false;
        try
        {
            var targetTid  = GetWindowThreadProcessId(dialogHwnd, out _);
            var currentTid = GetCurrentThreadId();
            bool attached  = targetTid != 0 && AttachThreadInput(currentTid, targetTid, true);
            try
            {
                SetForegroundWindow(dialogHwnd);
                if (editHwnd != IntPtr.Zero) SetFocus(editHwnd);
                Thread.Sleep(80);
                SendEnter();
                submitted = true;
            }
            finally
            {
                if (attached) AttachThreadInput(currentTid, targetTid, false);
            }
        }
        catch { }

        if (!submitted) return false;
        lock (_lock) { _succeeded.Add(dialogHwnd); }
        return true;
    }

    private static void SendEnter()
    {
        var inputs = new INPUT[2];
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].ki.wVk = VK_RETURN;
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].ki.wVk = VK_RETURN;
        inputs[1].ki.dwFlags = KEYEVENTF_KEYUP;
        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, string lParam);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    // CRITICAL: INPUT struct must be EXACTLY 40 bytes on x64.
    // type (4 bytes) + 4 bytes implicit padding + ki union at offset 8 (32 bytes) = 40.
    // Use LayoutKind.Explicit — Sequential with manual padding is wrong and will corrupt the stack.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_RETURN = 0x0D;
    private const uint WM_SETTEXT = 0x000C;
}
```

**Why `_succeeded` not `_attempted`:** If you add the dialog HWND on first attempt (regardless of whether fill+submit succeeded), then any transient failure permanently skips that dialog. The watcher must retry every 500ms until `filled && submitted`, THEN add to the set. The QB process takes several seconds to show the login dialog after launch — the watcher needs to keep polling.

### csproj
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <ApplicationIcon>app.ico</ApplicationIcon>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.*" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Json" Version="8.*" />
    <PackageReference Include="System.Text.Json" Version="8.*" />
  </ItemGroup>
</Project>
```

---

## Project 3: QBLockServiceSetup

**Purpose:** One-click installer for QBLockService. A WPF dialog that collects API keys, installs the service binary to `C:\QBLockService\`, writes `appsettings.json`, and runs `sc.exe` to create and start the Windows Service.

**Tech:** WPF .NET 8. Embeds `QBLockService.exe` as a resource (or copies from a sibling folder at build time).

### Install steps (MainWindow.xaml.cs)
1. Require elevation — if not admin, restart self with `runas`
2. Create `C:\QBLockService\`
3. Copy `QBLockService.exe` to install dir
4. Write `appsettings.json` with collected API keys and `"Urls": "http://0.0.0.0:5100"`
5. Run `sc.exe create QBLockService binPath= "C:\QBLockService\QBLockService.exe" start= auto`
6. Run `sc.exe start QBLockService`
7. Open Windows Firewall port 5100 via `netsh`

If service already exists: stop → delete → recreate.

---

## Build script (Build-Release.ps1)

```powershell
dotnet publish src\QBLockService    --configuration Release --runtime win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true --output release\QBLockService
dotnet publish src\QBLockManager   --configuration Release --runtime win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true --output release\QBLockManager
dotnet publish src\QBLockServiceSetup --configuration Release --runtime win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true --output release\QBLockServiceSetup
```

Build QBLockServiceSetup LAST — it must embed QBLockService.exe which is built first.
Kill `QBLockManager.exe` before building the client (`Stop-Process -Name QBLockManager -Force -ErrorAction SilentlyContinue`) to avoid "file locked" errors.

---

## .gitignore

```
bin/
obj/
release/
*.user
*.suo
.vs/
*.pdb
keys.tmp
appsettings.local.json
appsettings.Development.json
**/appsettings.json
Thumbs.db
.DS_Store
desktop.ini
```

---

## Deployment

- **Server:** Run `QBLockService-Setup.exe` as Administrator. Installs the service, opens firewall port 5100.
- **Workstation:** Copy `QBLockManager.exe` anywhere. Double-click. It self-installs to `%LOCALAPPDATA%\Programs\QBLockManager\` and runs the setup wizard. No .NET runtime required (self-contained).
- **Do NOT copy:** `.pdb` files or `appsettings.json` — the pdb is debug info only, and appsettings.json on each machine is written by the setup wizard with local credentials.

---

## Known gotchas

1. **QB login dialog is a MauiForm child window** — `FindWindow("MauiForm", "QuickBooks Desktop Login")` returns 0. Use UI Automation on the QB process's root windows and search descendants.

2. **WM_COMMAND IDOK is ignored by MauiForm** — must use `SendInput` with `VK_RETURN` to submit the login.

3. **`SetForegroundWindow` silently fails cross-process** — always call `AttachThreadInput(currentTid, targetTid, true)` before `SetFocus`/`SendInput`, and detach in a `finally` block.

4. **INPUT struct on x64 is 40 bytes** — the `ki` union starts at offset 8 (not 4) due to pointer-size alignment. Use `[StructLayout(LayoutKind.Explicit, Size=40)]` with `[FieldOffset(8)]` on the `ki` field. Sequential layout gets this wrong.

5. **EF Core `MigrateAsync()` in Windows Services** — unreliable because the CWD is not set to the exe directory until after `app.Run()`. Use `Directory.SetCurrentDirectory(AppContext.BaseDirectory)` before DB init and use raw SQL `CREATE TABLE IF NOT EXISTS` instead of migrations.

6. **FileKey must be consistent across machines** — use the path relative to a shared root, not the absolute local path (which differs by drive letter or UNC mount point per machine).
