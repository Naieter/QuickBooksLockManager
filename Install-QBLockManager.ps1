<#
.SYNOPSIS
    Installs the QBLockManager desktop app on a workstation.
    Run this script on each user workstation. Run Deploy-QBLockService.ps1 on the server.

.PARAMETER ServiceUrl
    URL of the central lock service (e.g., http://lock-server:5100)

.PARAMETER ApiKey
    Shared API key for authenticating with the lock service.

.PARAMETER QBPath
    Full path to QBW32.EXE on this workstation.

.PARAMETER WatchFolder
    The local path to the synced QuickBooks folder on this workstation.

.PARAMETER SharedRootPath
    The base path used to compute stable file keys (should be the same logical path across all workstations).

.PARAMETER InstallDir
    Where to install QBLockManager. Default: C:\Program Files\QBLockManager

.EXAMPLE
    .\Install-QBLockManager.ps1 `
        -ServiceUrl "http://FILESERVER:5100" `
        -ApiKey "your-api-key" `
        -QBPath "C:\Program Files\Intuit\QuickBooks Enterprise Solutions 24.0\QBW32.EXE" `
        -WatchFolder "C:\Users\$env:USERNAME\OneDrive\CompanyFiles" `
        -SharedRootPath "CompanyFiles"
#>

param(
    [Parameter(Mandatory)] [string] $ServiceUrl,
    [Parameter(Mandatory)] [string] $ApiKey,
    [string] $AdminApiKey = "",
    [string] $QBPath = "C:\Program Files\Intuit\QuickBooks Enterprise Solutions 24.0\QBW32.EXE",
    [Parameter(Mandatory)] [string] $WatchFolder,
    [string] $SharedRootPath = "",
    [string] $InstallDir = "$env:LOCALAPPDATA\Programs\QBLockManager",
    [string] $PublishDir = ".\release\QBLockManager"
)

$ErrorActionPreference = "Stop"

Write-Host "=== QuickBooks Lock Manager Workstation Installer ===" -ForegroundColor Cyan

# 1. Check publish output exists
if (-not (Test-Path $PublishDir)) {
    Write-Error "Publish output not found at: $PublishDir. Run: dotnet publish src\QBLockManager -c Release -o publish\QBLockManager"
}

# 2. Create install directory
Write-Host "Installing to: $InstallDir"
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null

# 3. Copy files (skip appsettings.json — user settings live in %APPDATA%\QBLockManager)
Get-ChildItem $PublishDir -File | Where-Object { $_.Name -ne "appsettings.json" } | ForEach-Object {
    Copy-Item $_.FullName -Destination $InstallDir -Force
}

# 4. Write appsettings.json
$settings = @{
    SetupCompleted         = $true
    LockServiceBaseUrl     = $ServiceUrl
    ApiKey                 = $ApiKey
    AdminApiKey            = $AdminApiKey
    IsAdminMode            = $false
    QuickBooksExePath      = $QBPath
    MultiFileMode          = $false
    WatchedFolders         = @(@{
        Path      = $WatchFolder
        Recursive = $false
        Label     = "QuickBooks Files"
    })
    SharedRootPath         = $SharedRootPath
    UserName               = $env:USERNAME
    DisplayName            = ""
    Email                  = ""
    HeartbeatIntervalSeconds = 20
    ServiceTimeoutSeconds    = 10
}

$settingsJson = $settings | ConvertTo-Json -Depth 5

# Write to APPDATA so the app can update it without admin, and also drop a
# copy in InstallDir as the read-only fallback for first-run defaults.
$appdataDir = "$env:APPDATA\QBLockManager"
New-Item -ItemType Directory -Force -Path $appdataDir | Out-Null
Set-Content -Path "$appdataDir\appsettings.json" -Value $settingsJson -Encoding utf8
Set-Content -Path "$InstallDir\appsettings.json" -Value $settingsJson -Encoding utf8

Write-Host "Configuration written." -ForegroundColor Green

# 5. Create desktop shortcut (user's desktop — no admin needed)
$WshShell = New-Object -ComObject WScript.Shell
$Shortcut = $WshShell.CreateShortcut("$env:USERPROFILE\Desktop\QuickBooks Lock Manager.lnk")
$Shortcut.TargetPath = "$InstallDir\QBLockManager.exe"
$Shortcut.WorkingDirectory = $InstallDir
$Shortcut.Description = "QuickBooks Lock Manager - Always open QB files through this app"
$Shortcut.Save()

Write-Host "Desktop shortcut created." -ForegroundColor Green

# 6. Create Start Menu entry (user's Start Menu — no admin needed)
$StartMenu = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs"
$Shortcut2 = $WshShell.CreateShortcut("$StartMenu\QuickBooks Lock Manager.lnk")
$Shortcut2.TargetPath = "$InstallDir\QBLockManager.exe"
$Shortcut2.WorkingDirectory = $InstallDir
$Shortcut2.Description = "QuickBooks Lock Manager"
$Shortcut2.Save()

Write-Host "Start Menu entry created." -ForegroundColor Green

Write-Host ""
Write-Host "=== Installation Complete ===" -ForegroundColor Green
Write-Host ""
Write-Host "IMPORTANT USER INSTRUCTIONS:" -ForegroundColor Yellow
Write-Host "  - Always open QuickBooks company files through 'QuickBooks Lock Manager'" -ForegroundColor Yellow
Write-Host "  - Do NOT open .QBW files directly from File Explorer" -ForegroundColor Yellow
Write-Host "  - Do NOT open company files from QuickBooks recent files list" -ForegroundColor Yellow
Write-Host "  - Opening files outside the Lock Manager bypasses all conflict protection" -ForegroundColor Yellow
Write-Host ""
Write-Host "If the lock service is unavailable, contact Nathan." -ForegroundColor Yellow
