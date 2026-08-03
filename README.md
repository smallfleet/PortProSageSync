# PortPro → Sage 50 (Canada) Invoice Sync

A Windows Service that periodically pulls invoices from PortPro's cloud API and
imports them into a server-based Sage 50 Canadian Edition company file via the
Sage 50 SDK, with validation/auto-matching of customers, items/services, and GL
accounts.

## Projects

| Project | Purpose |
|---|---|
| `PortProSage.Core` | Shared library: PortPro API client, Sage 50 SDK wrapper, validation, sync orchestration, local state DB. |
| `PortProSage.Service` | The Windows Service itself (a .NET Generic Host `BackgroundService`). |
| `PortProSage.Trigger` | A small CLI you run to request a one-off manual sync (by last-changed date, invoice number range, or invoice complete date range). |

## How it works

1. **Automatic sync** — every `Sync:PollingIntervalMinutes` (default 15), the
   service asks PortPro for invoices whose `updatedAt` ("last changed") falls
   between a stored watermark and now, imports whatever validates, and advances
   the watermark. State lives in a local SQLite file (`Sync:StateDatabasePath`),
   so a service restart doesn't re-scan from scratch or lose track of what's
   already been imported.

2. **Manual sync** — run `PortProSage.Trigger.exe` with `--mode lastchanged`,
   `--mode invoicerange`, or `--mode completedate` plus the relevant dates or
   invoice numbers. This drops a small JSON request file into the service's
   trigger folder; the service polls that folder every ~15 seconds, processes
   any request it finds, and writes a `*.result.json` report next to the
   archived request describing what was fetched/imported/skipped/failed.

   A file-drop was used instead of a REST/named-pipe endpoint on the service
   itself so the trigger tool has zero network/IPC setup — it just needs
   filesystem access to the same folder (works fine even if run from a
   scheduled task or another machine with a mapped drive).

3. **Validation & auto-matching** — before importing, each invoice is checked
   against Sage 50:
   - **Customer**: matched by company name (`caller.company_name` from
     PortPro). Missing customers are auto-created (per your answer) using
     `Sage50:DefaultReceivableAccount`.
   - **Items/services**: each PortPro charge line (`pricing[]`, e.g. "Base
     Price", detention, chassis fees) is matched by code/description. Missing
     ones are auto-created as service items, using the charge's own `glCode`
     if present, otherwise `Sage50:DefaultRevenueAccount`.
   - **GL accounts**: whatever account gets used (item's own account, charge's
     `glCode`, or the default) is confirmed to actually exist in the chart of
     accounts before the invoice is imported — if it doesn't, that invoice is
     skipped with a validation error rather than silently miscoded.
   - Every invoice that fails validation, or that already exists in the local
     state DB, is skipped (not imported) and recorded in the result report;
     nothing is ever double-imported.

## Two things to verify before you rely on this

I built this against what's publicly documented plus the field names/values
visible in your existing "PortPro Sage 50 Connector" tool's settings screen,
but two things are still worth double-checking:

1. **PortPro's exact filter query parameters.** The base URL and token
   endpoints (`/token`, `/generate-new-token`) are now confirmed from your
   screenshot, and there's no client id/secret step — PortPro issues an
   Access Token + Refresh Token pair directly (see Configuration below). What's
   still unconfirmed is the exact filter parameter names on `GET /invoices`
   itself — `PortProClient.BuildQueryString` sends best-guess names
   (`updatedAtFrom/To`, `completedDateFrom/To`, `referenceNumberFrom/To`).
   Confirm these against your PortPro API reference (or with your PortPro
   rep) and adjust that one method — everything downstream is unaffected.

2. **The Sage 50 SDK's exact object/method names.** Your screenshot's "Sage
   App Name" / "Sage App ID" fields confirm the SDK uses an app-registration
   pattern (shared with the US "Peachtree" API): the calling app identifies
   itself via `Begin(appId, appName)` before it can `Open()` a company file,
   and Sage 50 prompts the user to grant that app access the first time it
   connects. `Sage50Client.cs` now reflects that shape
   (`Session.Begin(AppId, AppName)` → `Session.Open(path, user, password)`),
   but the exact method/property names still depend on your installed SDK
   version — confirm against the SDK's own help file/sample apps and adjust
   the `// TODO`-marked calls if they differ.

   **One thing I can't confirm from a screenshot:** whether the `PPS50` App
   ID shown is safe for *this* service to reuse, or whether it's tied to that
   other connector tool's own registration with Sage. If that tool is a
   separate product (not something you're replacing with this one), you may
   need your own registered App ID rather than reusing theirs — worth
   checking before your first real connect.

## Sage 50 SDK installation & version matching


The Sage 50 SDK **must exactly match your installed Sage 50 product version** —
Sage does not support running a mismatched SDK, and two SDK versions can't be
installed side by side. As of this writing, Sage 50 Canada's full product is
at **2026.2**, while the public SDK download portal was still listing the
**2026.1** SDK installer — check the portal at install time rather than
assuming either number, since Sage updates these independently:

- Installed version: Sage 50 → **Help > About Sage 50 Accounting**
- SDK download: search "Sage 50 Canadian Edition SDK Download Portal" on
  `ca-kb.sage.com` (publicly downloadable; a free Sage developer/partner
  account gets you the full docs/support)

The service now checks this automatically at startup: `Sage50Client` looks up
`Sage50:SdkProgId` in the Windows registry, logs the actual installed file
version, and (if you set `Sage50:ExpectedSdkVersion`, e.g. `"2026.2"`) logs a
warning when they don't match. This is diagnostic only — it won't block
startup, but it turns a confusing COM error mid-sync into a clear log message
up front. Check `Sync.LogFolder` after first start for a line like:

```
Detected Sage 50 SDK: ProgID=SageData50.Session, file=C:\...\Interop.SomeSage50.dll, version=2026.2.xxxx
```

If the ProgID isn't found at all, the log will say so explicitly instead of
throwing a raw `COMException` later.

## Environments: Dev now, Production later

This config is set up so the same build can run on the dev server today and
be promoted to production later without code changes:

- `appsettings.json` — shared, non-environment-specific defaults/schema.
- `appsettings.Development.json` — layered on top when running on the dev
  server: faster polling (2 min) for quick feedback, verbose (`Debug`)
  logging, and separate `dev\` subfolders for logs/state/triggers so dev runs
  never collide with production data.
- `appsettings.Production.json` — layered on top on the production server:
  normal polling, `Information`-level logging, and placeholders
  (`REPLACE_ME_PRODUCTION_...`) for the production `.SAI` path and expected
  SDK version, which you fill in once you know the production server's setup.

Which one loads is controlled by the `DOTNET_ENVIRONMENT` variable, which
defaults to `Production` if unset — so **on the dev server, set it explicitly**:

```powershell
# For running interactively on the dev server:
$env:DOTNET_ENVIRONMENT = "Development"
dotnet run --project PortProSage.Service

# For installing as a Windows Service on the dev server (env var set at the service level,
# since services don't inherit your logged-in session's environment variables):
.\install-service.ps1 -Action Install -BinPath "C:\PortProSageSync\bin\PortProSage.Service.exe" -Environment Development
```

When you're ready to promote to the production server, install there without
`-Environment` (it defaults to `Production`), or pass `-Environment Production`
explicitly, and fill in `appsettings.Production.json`'s `REPLACE_ME_...`
values first. The startup log line (`PortProSageSync starting in {Environment}
environment...`) confirms which config actually loaded — check it right after
first start on any new server as a sanity check.

### Promotion checklist

Before moving from dev to production, re-verify each of these rather than
assuming dev settings carry over safely:

- [ ] `PortPro` credentials — production PortPro account will have its own
      `AccessToken`/`RefreshToken` pair, different from the dev/sandbox one.
- [ ] `Sage50:CompanyDataPath` — points at the production company's `.SAI` file,
      not the dev/test company.
- [ ] `Sage50:ExpectedSdkVersion` — matches whatever Sage 50 SDK version is
      actually installed on the production server (may differ from dev).
- [ ] `Sync:StateDatabasePath` — a fresh, empty production path. Don't reuse
      the dev SQLite file — it holds a dev watermark and dev-only "already
      imported" records that have no relationship to production PortPro data.
- [ ] `Sync:InitialLookbackDays` — reconsider before first production run;
      the dev value (30 days) is for convenient re-testing, not a production
      backfill window.
- [ ] `Sage50:AutoCreateCustomers` / `AutoCreateItems` — confirm this is still
      the policy you want once real customer/GL data is on the line, not just
      convenient for dev testing.
- [ ] Don't copy the dev server's `appsettings.json` secrets verbatim — set
      `PortPro:AccessToken`/`RefreshToken` and `Sage50:Password` via
      environment variables on production
      (`PortProSage__PortPro__AccessToken`, etc.) rather than plaintext file,
      per the note in Configuration below.

## Configuration

Edit `PortProSage.Service/appsettings.json`. At minimum:

- `PortPro.AccessToken` / `RefreshToken` — PortPro issues these directly (no
  client id/secret in this account's setup); get the current pair from
  PortPro's own integration/API settings screen, or from your PortPro rep.
- `Sage50.CompanyDataPath`, `UserName`, `Password`, `SdkProgId`
- `Sage50.AppId` / `AppName` — the Sage 50 SDK requires the calling app to
  register itself before it can open a company file; confirm whether you
  should use your own registered App ID or the one your existing connector
  tool uses (see the caveat above).
- `Sage50.DefaultRevenueAccount`, `DefaultReceivableAccount`

**Don't leave real secrets in plaintext appsettings.json on a shared server.**
Override `PortPro:AccessToken`/`RefreshToken` and `Sage50:Password` via
environment variables (`PortProSage__PortPro__AccessToken`, etc., using the
standard ASP.NET Core config double-underscore convention) or Windows-protected
config, and leave placeholders in the checked-in file. This matters even more
once this is in a git repo — real tokens/passwords pasted into
`appsettings.json` and committed will sit in git history even if you remove
them later. Keep placeholders in the committed files; set real values via
environment variables on each machine instead.

## Staged testing plan

Test each layer in isolation before relying on the full pipeline — that way a
failure points at exactly one thing, not "somewhere in PortPro, Sage 50, or
the glue code." All of these run with `DOTNET_ENVIRONMENT=Development` (dev
overlay defaults `Sage50:DryRun` to `true`, so nothing writes to Sage 50 until
you deliberately flip it).

1. **Build sanity.** `dotnet restore && dotnet build` on the dev server
   (needs internet access to nuget.org). Fix any compile errors before
   anything else — in particular, `Sage50Client.cs` has several `// TODO`
   COM calls that assume a certain SDK object model; if your installed SDK's
   real API differs, this is where it'll show up as a runtime (not compile)
   error the first time `--diagnose sage50` runs, since the calls are
   late-bound (`dynamic`).

2. **PortPro connectivity only** — no Sage 50 involved at all:
   ```powershell
   $env:DOTNET_ENVIRONMENT = "Development"
   dotnet run --project PortProSage.Service -- --diagnose portpro
   ```
   Confirms auth (`AccessToken`/`RefreshToken`) and the invoices
   endpoint work, and prints a few sample invoices it fetched. If this fails,
   the error message points at config or the (unverified) query parameter
   names in `PortProClient.BuildQueryString`.

3. **Sage 50 SDK connectivity only** — no PortPro call made:
   ```powershell
   dotnet run --project PortProSage.Service -- --diagnose sage50
   ```
   Confirms the SDK ProgID resolves, the company file opens, and a read-only
   customer lookup works. This is where you'll find out quickly whether
   `Sage50:SdkProgId` and the `// TODO` method names in `Sage50Client.cs`
   need adjusting for your actual installed SDK.

4. **Full pipeline, dry run** — both stages 2 and 3 passing, `Sage50:DryRun`
   still `true`:
   ```powershell
   dotnet run --project PortProSage.Service
   # in another shell, once it's running:
   dotnet run --project PortProSage.Trigger -- --folder "C:\PortProSageSync\dev\requests" --mode invoicerange --start <a real invoice #> --end <same #>
   ```
   Check `<folder>\processed\<requestId>.result.json` — you should see
   validation/matching decisions and "DRY RUN - would import as DRYRUN-..."
   messages, with nothing actually written to Sage 50. This is the stage to
   sanity-check that customer/item matching and account resolution behave
   the way you expect, before any real write.

5. **First real write, small and manual.** Set `Sage50:DryRun` to `false`
   (still on the dev server / dev company file), then re-run the same
   `invoicerange` trigger from step 4 for one known invoice. Verify the
   resulting invoice in Sage 50 directly (customer, lines, amounts, accounts)
   before trusting a larger batch.

6. **Automatic polling.** Leave the service running and let its normal
   `PollingIntervalMinutes` cycle pick up a newly changed invoice from
   PortPro, to confirm the watermark/auto-poll path (not just manual
   triggers) works end to end.

Only after all of that would I call it ready for the production checklist
below.

## Build & run



```powershell
# Restore/build (requires internet access to nuget.org) - run from the repo root,
# where PortProSageSync.sln lives, so MSBuild knows what to build:
dotnet restore
dotnet build

# Run interactively for testing (Ctrl+C to stop)
dotnet run --project PortProSage.Service

# Publish for deployment
dotnet publish PortProSage.Service -c Release -o C:\PortProSageSync\bin --self-contained false
dotnet publish PortProSage.Trigger -c Release -o C:\PortProSageSync\bin --self-contained false

# Install as a Windows Service (elevated PowerShell)
.\install-service.ps1 -Action Install -BinPath "C:\PortProSageSync\bin\PortProSage.Service.exe"
.\install-service.ps1 -Action Start
```

## Manual sync examples

```powershell
# Invoices changed in the last month
PortProSage.Trigger.exe --folder "C:\PortProSageSync\requests" --mode lastchanged --from 2026-07-01 --to 2026-08-01

# A specific invoice number range
PortProSage.Trigger.exe --folder "C:\PortProSageSync\requests" --mode invoicerange --start INV-1000 --end INV-1050

# Invoices completed in a date range
PortProSage.Trigger.exe --folder "C:\PortProSageSync\requests" --mode completedate --from 2026-07-15 --to 2026-07-31
```

## Logs & troubleshooting

- Rolling daily logs: `Sync.LogFolder` (default `C:\PortProSageSync\logs`).
- Local state (watermark + imported-invoice ledger): `Sync.StateDatabasePath`
  — a plain SQLite file, inspectable with any SQLite browser if you need to
  manually clear a "stuck" watermark or force a re-import.
- Every manual trigger's outcome: `<TriggerFolder>\processed\<requestId>.result.json`.

## Extending validation

`InvoiceValidationService` is the single place invoice-level business rules
live (customer/item/account matching, auto-create policy). If you need
additional checks — e.g. rejecting invoices below a minimum amount, requiring
specific reference fields, or currency matching — add them there; the
orchestrator and Sage 50 client don't need to change.

## Fixyee (placeholder for later)

A config placeholder and stub client are already in place for the future
Fixyee integration, so wiring it in later shouldn't require restructuring:

- `PortProSage:Fixyee:ApiKey` / `BaseUrl` / `Enabled` in `appsettings.json`
  (all placeholders right now — `Enabled` defaults to `false`).
- `FixyeeSettings` in `PortProSage.Core/Config/AppSettings.cs`.
- `FixyeeClient` in `PortProSage.Core/Fixyee/FixyeeClient.cs` — attaches the
  API key to outgoing requests (currently assumes Bearer auth as a
  placeholder; confirm Fixyee's real auth scheme once you have their docs)
  and includes a stub `TestConnectionAsync` you can point at a real endpoint.

Nothing currently calls `FixyeeClient` — it's registered in DI
(`Program.cs`) but otherwise inert until you fill in real endpoints and
decide how it fits the sync pipeline (e.g. a second target alongside Sage 50,
or its own parallel orchestrator if its data shape differs enough).

