<#
.SYNOPSIS
  Builds PortProSageSyncInstaller.exe - a single, double-clickable installer
  with the published Service/Trigger/Admin binaries baked in via ps2exe's
  -embedFiles, so the target machine needs nothing but .NET Framework 4.8
  and (for the Admin app) the .NET 10 Desktop Runtime already installed -
  no dotnet SDK, no source checkout, no manual dotnet publish.

.DESCRIPTION
  Run this FROM the repo root (where PortProSageSync.sln lives) on a
  machine with the dotnet SDK and a lib\Sage50SDK populated with the SDK
  version matching whatever machine will actually RUN the installed
  service - those DLLs get baked into the Service payload here, same as
  Install-Production.ps1's publish step.

  Publishes into .\dist\payload (NEVER into C:\PortProSageSync - this must
  not touch or read this dev machine's live install, which has real test
  data/state.db from ongoing development) then embeds every published file
  into installer\Install-PortProSageSync.ps1 via the ps2exe module
  (installed automatically if missing).

.EXAMPLE
  .\Build-Installer.ps1
#>

param(
    [string]$OutputExe = (Join-Path $PSScriptRoot "dist\PortProSageSyncInstaller.exe")
)

$ErrorActionPreference = "Stop"
$repoRoot = $PSScriptRoot
$stagingRoot = Join-Path $repoRoot "dist\payload"

Write-Host "=== Building PortProSageSyncInstaller.exe ===" -ForegroundColor Cyan

# ---- 0. Sanity check the Sage 50 SDK is populated - see Install-Production.ps1 ----
$sdkDll = Join-Path $repoRoot "lib\Sage50SDK\Sage_SA.SDK.dll"
if (-not (Test-Path $sdkDll)) {
    Write-Error "lib\Sage50SDK\Sage_SA.SDK.dll not found. Populate lib\Sage50SDK with the Sage 50 SDK matching your PRODUCTION Sage 50 version before building - see DEPLOYMENT.md."
    exit 1
}
Write-Host "[OK] Sage 50 SDK found." -ForegroundColor Green

# ---- 1. ps2exe module ----
if (-not (Get-Module -ListAvailable -Name ps2exe)) {
    Write-Host "Installing ps2exe module..." -ForegroundColor Cyan
    Install-PackageProvider -Name NuGet -MinimumVersion 2.8.5.201 -Force -Scope CurrentUser | Out-Null
    Install-Module -Name ps2exe -Scope CurrentUser -Force
}
Import-Module ps2exe

# ---- 2. Fresh staging area - never reuse a previous build's leftovers.
#         Remove CONTENTS rather than the root folder itself - some other
#         process (e.g. a background shell) may have $stagingRoot as its
#         current directory, which locks the folder handle even when empty
#         and makes removing the root itself fail. ----
if (Test-Path $stagingRoot) {
    Get-ChildItem -Path $stagingRoot -Force | Remove-Item -Recurse -Force
} else {
    New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null
}

# ---- 3. Publish each project (Release, framework-dependent) into staging -
#         NOT into C:\PortProSageSync. ----
$projects = [ordered]@{
    "Service" = "PortProSage.Service"
    "Trigger" = "PortProSage.Trigger"
    "Admin"   = "PortProSage.Admin"
}
foreach ($name in $projects.Keys) {
    $out = Join-Path $stagingRoot $name
    Write-Host "`nPublishing $($projects[$name]) (Release)..." -ForegroundColor Cyan
    & dotnet publish (Join-Path $repoRoot $projects[$name]) -c Release -o $out --self-contained false
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish ($($projects[$name])) failed with exit code $LASTEXITCODE." }
}

if (-not (Test-Path (Join-Path $stagingRoot "Service\Sage_SA.SDK.dll"))) {
    throw "Sage_SA.SDK.dll did not land in the published Service output - the publish step did not pick up lib\Sage50SDK as expected."
}
Write-Host "`n[OK] All projects published." -ForegroundColor Green

# Docs alongside the Service payload - same as Install-Production.ps1, makes
# the Admin app's Help button work on this kind of install too.
foreach ($doc in @("README.md", "USER_GUIDE.md", "DEPLOYMENT.md")) {
    $docSource = Join-Path $repoRoot $doc
    if (Test-Path $docSource) {
        Copy-Item $docSource (Join-Path $stagingRoot "Service\$doc") -Force
    }
}

# ---- 4. Build the -embedFiles manifest: every staged file -> its fixed
#         C:\PortProSageSync\<Service|Trigger|Admin>\<relative path> target.
#         This is what makes "install into the same folder, no changes
#         needed after unwrapping" true - the target paths ARE the paths
#         appsettings.json's own defaults already expect.
#
#         ps2exe requires SOURCE file names (basenames) to be unique across
#         the whole table - it embeds each source as a .NET resource keyed
#         by filename. Service/Trigger/Admin are published independently and
#         share dependencies (PortProSage.Core.dll, the Sage SDK, Newtonsoft.Json,
#         etc.), so the same basename legitimately appears at multiple target
#         paths. Work around it by staging a uniquely-named copy of every file
#         in a flat manifest folder and embedding THAT as the source, while the
#         target (the real extraction path) is unaffected. ----
Write-Host "`nBuilding embedded-file manifest..." -ForegroundColor Cyan
$manifestRoot = Join-Path $stagingRoot "_embedManifest"
New-Item -ItemType Directory -Path $manifestRoot -Force | Out-Null
$embedFiles = @{}
$index = 0
foreach ($name in $projects.Keys) {
    $srcRoot = Join-Path $stagingRoot $name
    Get-ChildItem -Path $srcRoot -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($srcRoot.Length + 1)
        $target = "C:\PortProSageSync\$name\$relative"
        $index++
        $uniqueSource = Join-Path $manifestRoot ("{0:D5}_{1}" -f $index, $_.Name)
        Copy-Item $_.FullName -Destination $uniqueSource
        $embedFiles[$target] = $uniqueSource
    }
}
Write-Host "[OK] $($embedFiles.Count) file(s) will be embedded." -ForegroundColor Green

# ---- 5. Compile ----
$outDir = Split-Path $OutputExe -Parent
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
if (Test-Path $OutputExe) { Remove-Item $OutputExe -Force }

Write-Host "`nCompiling installer (this can take a while with $($embedFiles.Count) embedded files)..." -ForegroundColor Cyan
Invoke-ps2exe `
    -inputFile (Join-Path $repoRoot "installer\Install-PortProSageSync.ps1") `
    -outputFile $OutputExe `
    -embedFiles $embedFiles `
    -title "PortProSageSync Installer" `
    -description "Installs the PortPro-to-Sage 50 invoice sync (Service, Trigger, Admin) for SmallArc Inc." `
    -company "SmallArc Inc." `
    -product "PortProSageSync" `
    -version "1.0.0.0" `
    -requireAdmin `
    -x64

if (-not (Test-Path $OutputExe)) { throw "ps2exe did not produce $OutputExe." }

$sizeMb = [Math]::Round((Get-Item $OutputExe).Length / 1MB, 1)
Write-Host "`n=== Done ===" -ForegroundColor Cyan
Write-Host "Installer: $OutputExe ($sizeMb MB)"
Write-Host "Hand this ONE file to whoever is installing - double-click it (as Administrator) on the target machine."
Write-Host "It installs to C:\PortProSageSync every time - no path questions, nothing to reconfigure after unwrapping."
