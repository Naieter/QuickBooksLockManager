<#
.SYNOPSIS
    Deploys the QBLockService as a Windows Service on the server.

.PARAMETER ApiKey
    Shared API key clients must send in X-Api-Key header.

.PARAMETER AdminApiKey
    Separate admin key required for force-unlock operations.

.PARAMETER DbPath
    Path to the SQLite database file.

.PARAMETER Port
    Port to listen on. Default: 5100

.PARAMETER InstallDir
    Where to install the service. Default: C:\QBLockService

.PARAMETER PublishDir
    Path to the dotnet publish output.

.EXAMPLE
    .\Deploy-QBLockService.ps1 `
        -ApiKey "long-random-api-key" `
        -AdminApiKey "different-long-admin-key" `
        -Port 5100
#>

param(
    [Parameter(Mandatory)] [string] $ApiKey,
    [Parameter(Mandatory)] [string] $AdminApiKey,
    [string] $DbPath = "C:\QBLockService\data\qblocks.db",
    [int]    $Port = 5100,
    [string] $InstallDir = "C:\QBLockService",
    [string] $PublishDir = ".\release\QBLockService",
    [int]    $LockTimeoutMinutes = 5
)

$ErrorActionPreference = "Stop"
$ServiceName = "QBLockService"

Write-Host "=== QuickBooks Lock Service Deployer ===" -ForegroundColor Cyan

# 1. Check publish output
if (-not (Test-Path $PublishDir)) {
    Write-Error "Publish output not found at: $PublishDir. Run: dotnet publish src\QBLockService -c Release -o publish\QBLockService"
}

# 2. Stop existing service if running
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Stopping existing service..."
    Stop-Service -Name $ServiceName -Force
    Start-Sleep -Seconds 2
}

# 3. Create directories
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path $DbPath) | Out-Null

# 4. Copy publish output
Copy-Item "$PublishDir\*" -Destination $InstallDir -Recurse -Force

# 5. Write appsettings.json
$settings = @{
    Logging = @{
        LogLevel = @{
            Default              = "Information"
            "Microsoft.AspNetCore" = "Warning"
        }
    }
    AllowedHosts = "*"
    Urls         = "http://0.0.0.0:$Port"
    ApiKey       = $ApiKey
    AdminApiKey  = $AdminApiKey
    Database     = @{ Path = $DbPath }
    LockSettings = @{
        TimeoutMinutes            = $LockTimeoutMinutes
        StaleCheckIntervalSeconds = 60
    }
}

$settingsJson = $settings | ConvertTo-Json -Depth 5
Set-Content -Path "$InstallDir\appsettings.json" -Value $settingsJson -Encoding utf8
Write-Host "Config written."

# 6. Install / update Windows Service
if ($existing) {
    # Update binary path
    sc.exe config $ServiceName binpath= "$InstallDir\QBLockService.exe" | Out-Null
} else {
    sc.exe create $ServiceName `
        binpath= "$InstallDir\QBLockService.exe" `
        start= auto `
        DisplayName= "QuickBooks Lock Service" | Out-Null
    sc.exe description $ServiceName "Central lock manager for QuickBooks company files." | Out-Null
}

# 7. Configure recovery: restart on failure
sc.exe failure $ServiceName reset= 60 actions= restart/5000/restart/5000/restart/5000 | Out-Null

# 8. Start service
Start-Service -Name $ServiceName
Start-Sleep -Seconds 3

$svc = Get-Service -Name $ServiceName
if ($svc.Status -eq "Running") {
    Write-Host "Service is running on port $Port." -ForegroundColor Green
} else {
    Write-Warning "Service may not have started. Check: Get-EventLog Application -Source QBLockService"
}

Write-Host ""
Write-Host "=== Deployment Complete ===" -ForegroundColor Green
Write-Host "Health check: http://localhost:$Port/health"
Write-Host "Swagger UI:   http://localhost:$Port/swagger (development only)"
