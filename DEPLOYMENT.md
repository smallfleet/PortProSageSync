# Production Deployment Guide

This covers moving `PortProSageSync` from this development machine to a real
production server. See `README.md` for how the system works day-to-day; this
file is specifically about getting it *installed* somewhere else.

## The short version

```powershell
# Elevated PowerShell, from the repo root:
.\Install-Production.ps1 -InstallPath "C:\PortProSageSync" -StartService
```

That one command publishes a Release build, lays out the required folders,
scaffolds `appsettings.Local.json` if one doesn't already exist, and
registers + starts the Windows Service. See "One-shot installer" below for
exactly what it does and its other options (`-IncludeAdmin`, `-SkipPublish`).
The rest of this document explains what it's doing and why, plus the manual
steps if you'd rather do it by hand.

## What you need, and why

### 1. A Release build, not Debug

Everything built during development lives under `bin\Debug\...` - slower,
includes debug symbols, not meant for production. Production needs a
`dotnet publish -c Release` build instead:

```powershell
dotnet publish PortProSage.Service -c Release -o C:\ProdBuild\Service --self-contained false
```

`PortProSage.Core.csproj` is set up so the Sage 50 SDK DLLs
(`lib\Sage50SDK\Sage_SA.*.dll`, `Sage.*.dll`, `Simply.*.dll`, `SageID.*.dll`,
`log4net.dll`, `Microsoft.Web.WebView2.*.dll`) get copied into the publish
output automatically - you don't hand-pick files, `dotnet publish` does it.

**Important:** whatever machine you run `dotnet publish` FROM must have
`lib\Sage50SDK` populated with the SDK version matching the Sage 50 product
installed on the machine that will actually RUN the service - those DLLs get
baked into the publish output at publish time, not resolved later at
runtime. If you're publishing from this dev machine and production runs the
same Sage 50 version (2026.2, confirmed from this machine's logs), the
`lib\Sage50SDK` folder already here is fine as-is.

### 2. Files/folders that need to exist on the production server

**Required - the published Service output**, i.e. everything
`dotnet publish` puts in its output folder: `PortProSage.Service.exe`,
`PortProSage.Core.dll`, the Sage 50 SDK DLLs, and `appsettings.json`.

**Required but NOT part of the publish output - `appsettings.Local.json`**.
This file is git-ignored and per-machine on purpose; it never gets built or
copied automatically. Create it fresh on the production server by copying
`appsettings.Local.json.example` to `appsettings.Local.json` (same folder as
the .exe) and filling in the **real** production values:

- `PortPro.AccessToken` / `RefreshToken`
- `Sage50.CompanyDataPath` (the real production `.sai` file path)
- `Sage50.UserName` / `Password` (see "dedicated Sage 50 account" below)

Never copy a dev/test `appsettings.Local.json` to production - it has
credentials and a company file path pointed at whatever you were testing
against here.

**Optional - the Admin app**, if you want the GUI configuration/monitoring
tool on the production box too:

```powershell
dotnet publish PortProSage.Admin -c Release -o C:\ProdBuild\Admin --self-contained false
```

Needs the .NET 10 Desktop Runtime on that machine - independent of the
Service's .NET Framework 4.8 requirement.

**Not needed in production:** `PortProSage.Trigger` (a CLI alternative to
the Admin app's Manual Run) - skip it unless you specifically want
scripted/scheduled-task triggers instead of the GUI.

### 3. Environment prerequisites on the production server

- **Sage 50 2026.2 installed and licensed**, with the real production
  company file present.
- **A dedicated Sage 50 user account for the service** - not the same login
  a human uses interactively. Sage 50 rejects a second simultaneous session
  under one username, even in multi-user mode; this bit us during
  development (see `Sage50Client.cs`'s `ConnectAsync` comments).
- **.NET Framework 4.8 runtime** for the Service (Windows Server usually has
  this already - verify with `dotnet --list-runtimes` or Programs & Features).
- **.NET 10 Desktop Runtime** only if deploying the Admin app there too.
- Write access to whatever folders `appsettings.json`'s `Sync` section
  points at (`TriggerFolder`, `LogFolder`, `StateDatabasePath`,
  `FailedTransactionsFolder`) - the one-shot installer creates these under
  `-InstallPath` automatically.

### 4. Also worth checking on the production Sage 50 company file specifically

It's a different file from whatever you tested against here - it can have
its own account numbers, tax codes, or (as we found the hard way during
development) its own **"Do Not Allow Transactions Dated Before"** cutoff
under Setup > Settings > Company > System. Worth confirming that setting
won't block whatever date range you first sync.

## One-shot installer: `Install-Production.ps1`

Located at the repo root, next to `PortProSageSync.sln`. Run from an
elevated PowerShell session, from the repo root:

```powershell
.\Install-Production.ps1 -InstallPath "C:\PortProSageSync" -StartService
```

What it does, in order:

1. Confirms it's running elevated (required to register a Windows Service).
2. Confirms `lib\Sage50SDK\Sage_SA.SDK.dll` exists before publishing - fails
   fast with a clear message instead of producing a broken publish output.
3. Runs `dotnet publish` for `PortProSage.Service` (Release), and for
   `PortProSage.Admin` too if you pass `-IncludeAdmin`.
4. Verifies the Sage 50 SDK DLLs actually landed in the publish output
   (warns if not - usually means `lib\Sage50SDK` wasn't populated).
5. Creates the runtime folders (`requests`, `requests\processed`,
   `requests\manual`, `requests\auto-poll`, `logs`, `failed-transactions`)
   under `-InstallPath`.
6. If `appsettings.Local.json` doesn't already exist at the target, copies
   the `.example` template there and warns you to fill in real credentials.
   **Never overwrites an existing one** - safe to re-run for updates.
7. Registers the Windows Service - or, if one with the same name already
   exists, stops it and updates its binary path instead of erroring, so
   re-running this script is how you deploy an update.
8. Starts the service, but only with `-StartService` **and** only if
   `appsettings.Local.json` doesn't still contain placeholder
   `REPLACE_WITH_REAL_*` values - it won't start a half-configured service
   for you.

Options:

| Parameter | Default | Purpose |
|---|---|---|
| `-InstallPath` | `C:\PortProSageSync` | Where everything gets published/laid out. |
| `-ServiceName` | `PortProSageSync` | Windows Service name. |
| `-DisplayName` | `PortPro to Sage 50 Invoice Sync` | Windows Service display name. |
| `-IncludeAdmin` | off | Also publish `PortProSage.Admin` to `<InstallPath>\Admin`. |
| `-StartService` | off | Start the service after installing (subject to the placeholder check above). |
| `-SkipPublish` | off | Skip the `dotnet publish` step - just re-run folder setup / service registration against whatever's already in `<InstallPath>\Service`. |

Re-running the script (e.g. after a code update) is safe: it re-publishes,
leaves your real `appsettings.Local.json` alone, and updates the existing
service's binary path rather than failing because it already exists.

### Manual equivalent, if you'd rather not use the script

```powershell
# 1. Publish
dotnet publish PortProSage.Service -c Release -o C:\PortProSageSync\Service --self-contained false

# 2. Create appsettings.Local.json next to the published exe (copy the
#    .example, fill in real values) - not automated, has no source to copy from.

# 3. Create the runtime folders
New-Item -ItemType Directory -Force C:\PortProSageSync\requests, C:\PortProSageSync\requests\processed, `
    C:\PortProSageSync\requests\manual, C:\PortProSageSync\requests\auto-poll, C:\PortProSageSync\logs, `
    C:\PortProSageSync\failed-transactions

# 4. Register + start the Windows Service (elevated)
New-Service -Name "PortProSageSync" -BinaryPathName "C:\PortProSageSync\Service\PortProSage.Service.exe" `
    -DisplayName "PortPro to Sage 50 Invoice Sync" -StartupType Automatic
Start-Service -Name "PortProSageSync"
```

For quick stop/start/uninstall against an already-installed service, use
the built-in cmdlets directly - no extra script needed:

```powershell
Stop-Service -Name "PortProSageSync"
Start-Service -Name "PortProSageSync"
sc.exe delete "PortProSageSync"   # uninstall
```

To update the binary path of an existing service (e.g. after moving the
install), use `Install-Production.ps1` (it does this for you), or by hand:

```powershell
Stop-Service -Name "PortProSageSync"
sc.exe config "PortProSageSync" binPath= "`"C:\PortProSageSync\Service\PortProSage.Service.exe`""
```

## Before trusting it for real

Repeat the same staged-testing discipline used during development (see
README's "Staged testing plan"), but against the **production** Sage 50
file specifically:

1. `PortProSage.Service.exe --diagnose portpro`
2. `PortProSage.Service.exe --diagnose sage50`
3. `Sage50.DryRun: true` in `appsettings.json`, run a real batch through the
   Admin app or Trigger, verify the log/results look right.
4. Flip `DryRun` to `false`, test with **one** known invoice, verify it in
   Sage 50 by hand (customer, lines, amounts, accounts).
5. Only then trust a bigger batch, and let the automatic polling cycle run
   to confirm the watermark path works end to end.

## Updating an existing production install

```powershell
.\Install-Production.ps1 -InstallPath "C:\PortProSageSync" -StartService
```

Same command as the initial install - it republishes, leaves your real
`appsettings.Local.json` untouched, and updates the existing service's
binary path instead of failing. The service is stopped automatically as
part of updating its binary path; pass `-StartService` to have it start
back up when the script finishes, or start it yourself afterward.
