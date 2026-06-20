<#
.SYNOPSIS
    Builds self-contained single-file executables for QBLockManager and QBLockService.

    OUTPUT
    ------
    release\
        QBLockManager\
            QBLockManager.exe          ← hand this to each workstation user
        QBLockService\
            QBLockService.exe          ← run this on the server

.DESCRIPTION
    QBLockManager.exe is fully self-contained — no .NET installation required
    on the end-user workstation. Double-click to run. Setup wizard fires on first launch.

    QBLockService.exe is also self-contained. Deploy it to the server machine
    with Deploy-QBLockService.ps1.

.EXAMPLE
    .\Build-Release.ps1
#>

$ErrorActionPreference = "Stop"
$Root    = $PSScriptRoot
$Release = Join-Path $Root "release"

function Build-Project {
    param([string]$Project, [string]$Output)

    Write-Host ""
    Write-Host "Building $Project..." -ForegroundColor Cyan
    New-Item -ItemType Directory -Force -Path $Output | Out-Null

    & dotnet publish "$Root\src\$Project" `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        /p:PublishSingleFile=true `
        /p:IncludeNativeLibrariesForSelfExtract=true `
        --output $Output

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Build failed for $Project. Exit code: $LASTEXITCODE"
    }
    Write-Host "  → $Output" -ForegroundColor Green
}

# Clean
if (Test-Path $Release) {
    Remove-Item $Release -Recurse -Force
}

Build-Project "QBLockService"  "$Release\QBLockService"
Build-Project "QBLockManager"  "$Release\QBLockManager"

# Service setup must be built AFTER QBLockService — it embeds the service exe.
Build-Project "QBLockServiceSetup"  "$Release\QBLockServiceSetup"

Write-Host ""
Write-Host "════════════════════════════════════════" -ForegroundColor Green
Write-Host "  Build complete." -ForegroundColor Green
Write-Host "════════════════════════════════════════" -ForegroundColor Green
Write-Host ""
Write-Host "SERVER INSTALLER:      $Release\QBLockServiceSetup\QBLockService-Setup.exe"
Write-Host "WORKSTATION INSTALLER: $Release\QBLockManager\QBLockManager.exe"
Write-Host ""
Write-Host "Deployment:" -ForegroundColor Yellow
Write-Host "  Server:      Run QBLockService-Setup.exe as Administrator on the server."
Write-Host "               Enter API keys and click Install."
Write-Host "  Workstation: Copy QBLockManager.exe to the user's computer and run it."
Write-Host "               It will self-install to AppData and launch the setup wizard."
Write-Host ""
