<#
.SYNOPSIS
  One-shot production installer for PortProSageSync: publishes the Release
  build, lays out the runtime folder structure, scaffolds
  appsettings.Local.json if one doesn't already exist, and registers the
  Windows Service (optionally starting it). See DEPLOYMENT.md for the full
  explanation of what's needed and why.

.DESCRIPTION
  Run this FROM the repo root (where PortProSageSync.sln lives), in an
  elevated PowerShell session, on a machine that has the dotnet CLI and a
  lib\Sage50SDK populated with the SDK version matching whatever machine
  will actually RUN the service (those DLLs get baked into the publish
  output at publish time - see DEPLOYMENT.md).

  Safe to re-run for updates: an existing appsettings.Local.json is never
  overwritten, and an existing Windows Service has its binary path updated
  instead of the script failing because it already exists.

.PARAMETER InstallPath
  Where the published service (and, if -IncludeAdmin, the Admin app) and
  its runtime folders (requests/, logs/, failed-transactions/) get laid out.

.PARAMETER IncludeAdmin
  Also publish PortProSage.Admin to <InstallPath>\Admin.

.PARAMETER StartService
  Start the Windows Service once installed - but only if
  appsettings.Local.json doesn't still contain placeholder
  REPLACE_WITH_REAL_* values, so a half-configured install is never
  silently started.

.PARAMETER SkipPublish
  Skip the dotnet publish step - just (re)do folder setup and service
  registration against whatever's already sitting in <InstallPath>\Service.

.EXAMPLE
  .\Install-Production.ps1 -InstallPath "C:\PortProSageSync" -StartService

.EXAMPLE
  # Also deploy the Admin app, don't start the service yet (e.g. still need
  # to fill in appsettings.Local.json by hand first):
  .\Install-Production.ps1 -InstallPath "C:\PortProSageSync" -IncludeAdmin

.EXAMPLE
  # Redeploy an update to an already-installed service:
  .\Install-Production.ps1 -InstallPath "C:\PortProSageSync" -StartService
#>

param(
    [string]$InstallPath = "C:\PortProSageSync",
    [string]$ServiceName = "PortProSageSync",
    [string]$DisplayName = "PortPro to Sage 50 Invoice Sync",
    [switch]$IncludeAdmin,
    [switch]$StartService,
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"

# ---- 0. Elevation check - required to register/control a Windows Service ----
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "This script must be run from an elevated (Administrator) PowerShell session."
    exit 1
}

$repoRoot = $PSScriptRoot
$serviceOut = Join-Path $InstallPath "Service"
$adminOut = Join-Path $InstallPath "Admin"

Write-Host "=== PortProSageSync production install ===" -ForegroundColor Cyan
Write-Host "Repo root:    $repoRoot"
Write-Host "Install path: $InstallPath"
Write-Host ""

# ---- 1. Sanity check the Sage 50 SDK is populated before we publish - the
#         publish step bakes these DLLs into the output, so a missing SDK
#         here means a broken publish, not a runtime error later. ----
$sdkDll = Join-Path $repoRoot "lib\Sage50SDK\Sage_SA.SDK.dll"
if (-not $SkipPublish -and -not (Test-Path $sdkDll)) {
    Write-Error "lib\Sage50SDK\Sage_SA.SDK.dll not found. Populate lib\Sage50SDK with the Sage 50 SDK matching your PRODUCTION Sage 50 version before publishing - see DEPLOYMENT.md."
    exit 1
}

# ---- 2. Publish (Release) ----
if (-not $SkipPublish) {
    Write-Host "[OK] Sage 50 SDK found ($sdkDll)" -ForegroundColor Green
    Write-Host "`nPublishing PortProSage.Service (Release)..." -ForegroundColor Cyan
    & dotnet publish (Join-Path $repoRoot "PortProSage.Service") -c Release -o $serviceOut --self-contained false
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish (Service) failed with exit code $LASTEXITCODE." }

    if ($IncludeAdmin) {
        Write-Host "`nPublishing PortProSage.Admin (Release)..." -ForegroundColor Cyan
        & dotnet publish (Join-Path $repoRoot "PortProSage.Admin") -c Release -o $adminOut --self-contained false
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish (Admin) failed with exit code $LASTEXITCODE." }
    }
} else {
    Write-Host "Skipping publish (-SkipPublish) - using whatever is already in $serviceOut" -ForegroundColor Yellow
}

$serviceExe = Join-Path $serviceOut "PortProSage.Service.exe"
if (-not (Test-Path $serviceExe)) {
    throw "PortProSage.Service.exe not found at $serviceExe - publish it first (omit -SkipPublish), or check -InstallPath."
}

# ---- 3. Verify the Sage 50 SDK actually landed in the publish output ----
if (-not (Test-Path (Join-Path $serviceOut "Sage_SA.SDK.dll"))) {
    Write-Warning "Sage_SA.SDK.dll was not found in the publish output ($serviceOut). The service will fail to connect to Sage 50 until this is resolved - check that lib\Sage50SDK was populated before publishing."
}

# Copy the docs alongside the published Service - `dotnet publish` doesn't include
# them on its own, and the Admin app's "Help" button (top bar) looks for
# USER_GUIDE.md next to the configured Service folder, so this is what makes that
# button work on a production install too.
foreach ($doc in @("README.md", "USER_GUIDE.md", "DEPLOYMENT.md")) {
    $docSource = Join-Path $repoRoot $doc
    if (Test-Path $docSource) {
        Copy-Item $docSource (Join-Path $serviceOut $doc) -Force
    }
}

# ---- 4. Required runtime folders ----
Write-Host "`nCreating runtime folders..." -ForegroundColor Cyan
$folders = @(
    (Join-Path $InstallPath "requests"),
    (Join-Path $InstallPath "requests\processed"),
    (Join-Path $InstallPath "requests\manual"),
    (Join-Path $InstallPath "requests\auto-poll"),
    (Join-Path $InstallPath "logs"),
    (Join-Path $InstallPath "failed-transactions")
)
foreach ($f in $folders) {
    if (-not (Test-Path $f)) {
        New-Item -ItemType Directory -Path $f -Force | Out-Null
        Write-Host "[OK] Created $f" -ForegroundColor Green
    } else {
        Write-Host "[OK] Already exists: $f" -ForegroundColor Green
    }
}

# ---- 5. appsettings.Local.json - real per-machine secrets, never overwrite
#         an existing one, since it may already hold real production values. ----
$localSettingsPath = Join-Path $serviceOut "appsettings.Local.json"
$localSettingsExample = Join-Path $serviceOut "appsettings.Local.json.example"
if (Test-Path $localSettingsPath) {
    Write-Host "[OK] appsettings.Local.json already exists - leaving it untouched." -ForegroundColor Green
} elseif (Test-Path $localSettingsExample) {
    Copy-Item $localSettingsExample $localSettingsPath
    Write-Warning "Created $localSettingsPath from the template - EDIT IT with real production Sage 50 password and PortPro AccessToken/RefreshToken before starting the service."
} else {
    Write-Warning "No appsettings.Local.json or .example found in $serviceOut - create appsettings.Local.json there by hand with real secrets before starting the service (see DEPLOYMENT.md)."
}

# ---- 6. Register the Windows Service (idempotent - update, don't fail, if
#         it already exists, so re-running this script deploys an update) ----
Write-Host "`nRegistering Windows Service '$ServiceName'..." -ForegroundColor Cyan
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Service already exists - stopping it before updating the binary path." -ForegroundColor Yellow
    Stop-Service -Name $ServiceName -ErrorAction SilentlyContinue
    sc.exe config $ServiceName binPath= "`"$serviceExe`"" | Out-Null
    Write-Host "[OK] Updated binary path for existing service '$ServiceName'." -ForegroundColor Green
} else {
    New-Service -Name $ServiceName -BinaryPathName $serviceExe -DisplayName $DisplayName -StartupType Automatic | Out-Null
    Write-Host "[OK] Installed service '$ServiceName'." -ForegroundColor Green
}

# ---- 7. Start, but only if asked AND secrets look filled in ----
if ($StartService) {
    $localContent = if (Test-Path $localSettingsPath) { Get-Content $localSettingsPath -Raw } else { "" }
    if ($localContent -match "REPLACE_WITH_REAL") {
        Write-Warning "appsettings.Local.json still has placeholder REPLACE_WITH_REAL_* values - NOT starting the service. Fill in real credentials, then run: Start-Service -Name $ServiceName"
    } else {
        Start-Service -Name $ServiceName
        Write-Host "[OK] Service '$ServiceName' started." -ForegroundColor Green
    }
} else {
    Write-Host "`n-StartService not passed - service installed but not started." -ForegroundColor Yellow
}

Write-Host "`n=== Done ===" -ForegroundColor Cyan
Write-Host "Next steps:"
Write-Host "  1. Verify/edit $localSettingsPath with real production credentials (if just created)."
Write-Host "  2. Check $serviceOut\appsettings.json folder paths match -InstallPath if you changed it from the default."
Write-Host "  3. Test connectivity before relying on it:"
Write-Host "       & `"$serviceExe`" --diagnose portpro"
Write-Host "       & `"$serviceExe`" --diagnose sage50"
Write-Host "  4. Start (if not already): Start-Service -Name $ServiceName"
