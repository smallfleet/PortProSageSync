<#
.SYNOPSIS
  PortProSageSync installer - runs INSIDE the compiled PortProSageSyncInstaller.exe
  (see ..\Build-Installer.ps1, which compiles this script + the published
  application files into that single exe via ps2exe's -embedFiles).

.DESCRIPTION
  By the time this script body executes, ps2exe has ALREADY extracted every
  embedded file to its fixed target path under C:\PortProSageSync - that's
  what makes this "install into the same folder, no changes needed after
  unwrapping" - the application files are already exactly where
  appsettings.json's own default paths expect them to be, before a single
  line of this script runs.

  This script only handles what can't be baked in at build time:
    - Confirming extraction actually happened (sanity check).
    - Checking the runtime environment on THIS machine (.NET Framework 4.8,
      .NET Desktop Runtime) - these depend on what's installed on the target
      computer, not on anything the installer itself can control.
    - Creating the runtime folders (requests/logs/failed-transactions) -
      created empty if missing, never populated with anything from the
      build machine, so a fresh install starts genuinely empty.
    - Scaffolding appsettings.Local.json from its template, WITHOUT
      overwriting a real one from a previous install (this is what makes
      re-running this installer a safe update, not a wipe).
    - Registering the Windows Service.
    - Creating a desktop shortcut to the Admin app.

  Deliberately does NOT create or touch state.db anywhere - PortProSage.Core's
  SyncStateRepository creates it lazily (CREATE TABLE IF NOT EXISTS) the first
  time the Service actually runs. A fresh install has no state.db until then
  (genuinely "initial stage" - no watermark, no imported-invoice history). An
  update/reinstall on a machine that already has one is untouched, since this
  script never reads or writes that path - a real customer's sync history is
  never at risk from re-running this installer.
#>

$ErrorActionPreference = "Stop"

$InstallPath = "C:\PortProSageSync"
$ServiceName = "PortProSageSync"
$DisplayName = "PortPro to Sage 50 Invoice Sync"

Write-Host "=== PortProSageSync Installer ===" -ForegroundColor Cyan
Write-Host "Install path: $InstallPath`n"

# ---- 0. Elevation check - registering/controlling a Windows Service and
#         writing to C:\ requires it. The compiled exe also carries
#         ps2exe's -requireAdmin flag (see Build-Installer.ps1), which
#         should trigger a UAC prompt automatically before this script body
#         even starts - this is a defensive second check in case that's
#         ever bypassed (e.g. -extract). ----
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "This installer must be run as Administrator - right-click it and choose 'Run as administrator'." -ForegroundColor Red
    Read-Host "`nPress Enter to exit"
    exit 1
}

# ---- 1. Confirm ps2exe's embedded-file extraction actually happened ----
$serviceExe = Join-Path $InstallPath "Service\PortProSage.Service.exe"
$adminExe = Join-Path $InstallPath "Admin\PortProSage.Admin.exe"
if (-not (Test-Path $serviceExe)) {
    Write-Host "Expected application files were not found at $serviceExe." -ForegroundColor Red
    Write-Host "This installer may be corrupted or was blocked from extracting - try re-downloading it, or run it as Administrator if you haven't." -ForegroundColor Red
    Read-Host "`nPress Enter to exit"
    exit 1
}
Write-Host "[OK] Application files present at $InstallPath" -ForegroundColor Green

# ---- 2. Environment checks - what's installed on THIS machine, which the
#         build machine has no way to know in advance. ----
Write-Host "`nChecking this machine's environment..." -ForegroundColor Cyan

$netFxKey = "HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"
$netFxRelease = (Get-ItemProperty -Path $netFxKey -ErrorAction SilentlyContinue).Release
# 528040 = .NET Framework 4.8 (Windows 10 May 2019 Update and later); see
# Microsoft's own release-key reference table.
if (-not $netFxRelease -or $netFxRelease -lt 528040) {
    Write-Host "[WARNING] .NET Framework 4.8 was not detected. PortProSage.Service.exe and PortProSage.Trigger.exe require it." -ForegroundColor Yellow
    Write-Host "          Download: https://dotnet.microsoft.com/download/dotnet-framework/net48" -ForegroundColor Yellow
} else {
    Write-Host "[OK] .NET Framework 4.8+ detected." -ForegroundColor Green
}

$dotnetRuntimes = & dotnet --list-runtimes 2>$null
if (-not ($dotnetRuntimes -match "Microsoft\.WindowsDesktop\.App 10\.")) {
    Write-Host "[WARNING] .NET 10 Desktop Runtime was not detected. PortProSage.Admin.exe (this Admin app) requires it." -ForegroundColor Yellow
    Write-Host "          Download: https://dotnet.microsoft.com/download/dotnet/10.0" -ForegroundColor Yellow
} else {
    Write-Host "[OK] .NET 10 Desktop Runtime detected." -ForegroundColor Green
}

if (-not (Test-Path (Join-Path $InstallPath "Service\Sage_SA.SDK.dll"))) {
    Write-Host "[WARNING] Sage_SA.SDK.dll is missing from the installed Service folder - the Service will fail to connect to Sage 50 until this is resolved. Contact support; this installer may need to be rebuilt with the SDK populated." -ForegroundColor Yellow
} else {
    Write-Host "[OK] Sage 50 SDK present." -ForegroundColor Green
}

# ---- 3. Runtime folders - created empty if missing. Never populated from
#         the build machine, so a fresh install starts with nothing in them -
#         see this script's own doc comment for why state.db is handled the
#         same way (by not being created here at all). ----
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

# ---- 4. appsettings.Local.json - real per-machine secrets. Never overwrite
#         an existing one - this is what makes re-running this installer a
#         safe update on a machine that's already configured, not a wipe. ----
$localSettingsPath = Join-Path $InstallPath "Service\appsettings.Local.json"
$localSettingsExample = Join-Path $InstallPath "Service\appsettings.Local.json.example"
if (Test-Path $localSettingsPath) {
    Write-Host "`n[OK] appsettings.Local.json already exists - left untouched (this is an update, not a first install)." -ForegroundColor Green
} elseif (Test-Path $localSettingsExample) {
    Copy-Item $localSettingsExample $localSettingsPath
    Write-Host "`n[ACTION NEEDED] Created $localSettingsPath from the template." -ForegroundColor Yellow
    Write-Host "                Edit it with the real Sage 50 password and PortPro AccessToken/RefreshToken before starting the service." -ForegroundColor Yellow
} else {
    Write-Host "`n[WARNING] No appsettings.Local.json.example found to scaffold from - create $localSettingsPath by hand with real secrets before starting the service." -ForegroundColor Yellow
}

# ---- 5. Register the Windows Service - idempotent, so re-running this
#         installer updates an existing service instead of failing. ----
Write-Host "`nRegistering Windows Service '$ServiceName'..." -ForegroundColor Cyan
$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existingService) {
    Write-Host "Service already exists - stopping it and updating the binary path (this is an update)." -ForegroundColor Yellow
    Stop-Service -Name $ServiceName -ErrorAction SilentlyContinue
    sc.exe config $ServiceName binPath= "`"$serviceExe`"" | Out-Null
    Write-Host "[OK] Updated existing service '$ServiceName'." -ForegroundColor Green
} else {
    New-Service -Name $ServiceName -BinaryPathName $serviceExe -DisplayName $DisplayName -StartupType Automatic | Out-Null
    Write-Host "[OK] Installed service '$ServiceName'." -ForegroundColor Green
}

# ---- 6. Desktop shortcut for the Admin app - every logged-on user gets one
#         (CommonDesktopDirectory), matching the earlier "create a shortcut"
#         request handled by hand; this is what automates it going forward. ----
if (Test-Path $adminExe) {
    try {
        $shell = New-Object -ComObject WScript.Shell
        $shortcutPath = Join-Path ([Environment]::GetFolderPath("CommonDesktopDirectory")) "PortProSage Admin.lnk"
        $shortcut = $shell.CreateShortcut($shortcutPath)
        $shortcut.TargetPath = $adminExe
        $shortcut.WorkingDirectory = Split-Path $adminExe -Parent
        $shortcut.Description = "PortProSage Admin"
        $shortcut.Save()
        Write-Host "`n[OK] Desktop shortcut created for all users." -ForegroundColor Green
    } catch {
        Write-Host "`n[WARNING] Could not create the desktop shortcut: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

# ---- 7. Done - start automatically only if real secrets are already in
#         place (never start against placeholder credentials). ----
Write-Host "`n=== Installation complete ===" -ForegroundColor Cyan
Write-Host "Installed to: $InstallPath"

$localContent = if (Test-Path $localSettingsPath) { Get-Content $localSettingsPath -Raw } else { "" }
if ($localContent -match "REPLACE_WITH_REAL") {
    Write-Host "`nNEXT STEP: edit $localSettingsPath with real credentials, then start the service:" -ForegroundColor Yellow
    Write-Host "  Start-Service -Name $ServiceName" -ForegroundColor Yellow
} else {
    Write-Host "`nStarting the service..." -ForegroundColor Cyan
    try {
        Start-Service -Name $ServiceName
        Write-Host "[OK] Service '$ServiceName' started." -ForegroundColor Green
    } catch {
        Write-Host "[WARNING] Could not start the service automatically: $($_.Exception.Message)" -ForegroundColor Yellow
        Write-Host "          Start it by hand once ready: Start-Service -Name $ServiceName" -ForegroundColor Yellow
    }
}

Read-Host "`nPress Enter to exit"
