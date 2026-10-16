<#
.SYNOPSIS
    Builds and publishes a release that ALWAYS carries both deliverables:
        QBLockManager.exe          (workstations)
        QBLockService-Setup.exe    (server installer; embeds QBLockService.exe)

.DESCRIPTION
    Rule: every release ships the current client AND server, even when one of them is
    unchanged, so the newest release always has both for download.

    It also keeps the self-updaters working. QBLockService-Setup.exe and QBLockManager.exe
    each update themselves from releases/latest by looking for the asset with their own name,
    and they compare the release TAG to their own assembly version. So all three projects are
    stamped with the release version here; a setup stamped older than the tag would download
    itself again on every launch, forever.

    Unlike Build-Release.ps1 this does not delete the release\ folder (which can hold a local
    qblm-connect.json with live credentials).

    It does not commit or push. If source is uncommitted or unpushed it says so at the end,
    because the tag is created at the remote branch tip and would not contain those changes.

.PARAMETER Version
    Release version, three parts, no "v" (e.g. 1.6.9). Stamped into all three projects.

.PARAMETER Title
    Release title. Defaults to the tag.

.PARAMETER Notes
    Release notes (markdown).

.PARAMETER AttachToExisting
    The release for -Version already exists: upload whichever of the two assets it is missing
    instead of creating it.

.EXAMPLE
    .\Publish-Release.ps1 -Version 1.6.9 -Title "v1.6.9 - what changed" -Notes "Details."

.EXAMPLE
    .\Publish-Release.ps1 -Version 1.6.8 -AttachToExisting
#>
param(
    [Parameter(Mandatory = $true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [string]$Title = "",
    [string]$Notes = "",
    [switch]$AttachToExisting
)

$ErrorActionPreference = "Stop"
$Root    = $PSScriptRoot
$Tag     = "v$Version"
$Release = Join-Path $Root "release"

$Service = Join-Path $Release "QBLockService\QBLockService.exe"
$Client  = Join-Path $Release "QBLockManager\QBLockManager.exe"
$Setup   = Join-Path $Release "QBLockServiceSetup\QBLockService-Setup.exe"
$Required = @("QBLockManager.exe", "QBLockService-Setup.exe")

function Step($text) { Write-Host ""; Write-Host $text -ForegroundColor Cyan }

# 1. Stamp the release version into all three projects (lockstep).
Step "Stamping $Version into all three projects"
foreach ($p in "QBLockService", "QBLockManager", "QBLockServiceSetup") {
    $csproj = Join-Path $Root "src\$p\$p.csproj"
    $text   = [IO.File]::ReadAllText($csproj)
    $text   = $text -replace '<Version>[^<]*</Version>',               "<Version>$Version</Version>"
    $text   = $text -replace '<AssemblyVersion>[^<]*</AssemblyVersion>', "<AssemblyVersion>$Version.0</AssemblyVersion>"
    $text   = $text -replace '<FileVersion>[^<]*</FileVersion>',         "<FileVersion>$Version.0</FileVersion>"
    [IO.File]::WriteAllText($csproj, $text)
    Write-Host "  $p -> $Version"
}

# 2. Build. The setup embeds the service exe, so the service must be built first.
function Build-Project([string]$Project, [string]$OutDir) {
    Step "Building $Project"
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    # stdout is captured (shown only on failure); stderr is left alone - redirecting it can
    # turn harmless stderr lines into terminating errors under Windows PowerShell 5.1.
    $log = & dotnet publish "$Root\src\$Project" --configuration Release --runtime win-x64 `
        --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true `
        --output $OutDir
    if ($LASTEXITCODE -ne 0) {
        $log | Select-Object -Last 25 | ForEach-Object { Write-Host $_ }
        throw "Build failed for $Project (exit code $LASTEXITCODE)."
    }
    Write-Host "  -> $OutDir" -ForegroundColor Green
}
Build-Project "QBLockService"      (Join-Path $Release "QBLockService")
Build-Project "QBLockManager"      (Join-Path $Release "QBLockManager")
Build-Project "QBLockServiceSetup" (Join-Path $Release "QBLockServiceSetup")

# 3. Every artifact must carry the release version, or an updater will loop.
Step "Verifying versions"
foreach ($f in $Service, $Client, $Setup) {
    $v = (Get-Item $f).VersionInfo.FileVersion
    if ($v -ne "$Version.0") { throw "$(Split-Path $f -Leaf) is version $v, expected $Version.0" }
    "{0,-26} {1}   {2:N0} MB" -f (Split-Path $f -Leaf), $v, ((Get-Item $f).Length / 1MB) | Write-Host
}

# 4. Publish both assets.
Step "Publishing $Tag"
$assetPaths = @($Client, $Setup)
if ($AttachToExisting) {
    $have = @(& gh release view $Tag --json assets --jq '.assets[].name')
    if ($LASTEXITCODE -ne 0) { throw "Release $Tag does not exist; run without -AttachToExisting to create it." }
    foreach ($a in $assetPaths) {
        $name = Split-Path $a -Leaf
        if ($have -contains $name) { Write-Host "  $name already on $Tag - leaving it" }
        else {
            Write-Host "  uploading $name ..."
            & gh release upload $Tag $a --clobber
            if ($LASTEXITCODE -ne 0) { throw "Upload of $name failed." }
        }
    }
} else {
    if (-not $Title) { $Title = $Tag }
    & gh release create $Tag $Client $Setup --title $Title --notes $Notes
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed." }
}

# 5. Refuse to call it done unless the release really has both.
Step "Checking $Tag has both downloads"
$names = @(& gh release view $Tag --json assets --jq '.assets[].name')
foreach ($need in $Required) {
    if ($names -notcontains $need) { throw "RELEASE INCOMPLETE: $Tag is missing $need" }
    Write-Host "  OK  $need" -ForegroundColor Green
}

# 6. Git reminder: the tag lives at the remote branch tip.
Push-Location $Root
try {
    $dirty    = @(& git status --porcelain -- src).Count
    $unpushed = @(& git rev-list "origin/master..HEAD" 2>$null).Count
} finally { Pop-Location }
if ($dirty -or $unpushed) {
    Write-Host ""
    Write-Warning "Source is not fully on GitHub ($dirty uncommitted file(s), $unpushed unpushed commit(s)). $Tag points at the remote branch tip, which does not contain them. Commit and push, then move the tag to the new commit."
}
Write-Host ""
Write-Host "Done: https://github.com/Naieter/QuickBooksLockManager/releases/tag/$Tag" -ForegroundColor Green
