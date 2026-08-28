# PortProSage Admin — User Guide

This is the complete, walk-through reference for the PortProSage Admin application — the tool RS Rush Transfer Xpress Inc. uses to sync PortPro invoices into Sage 50 (Canadian Edition). If the blue **"?"** help icon next to a field doesn't answer your question, this guide should.

Open this guide any time from inside the app: click the **Help** button in the top bar.

> Looking for the technical/developer documentation instead (architecture, file formats, how the Windows Service itself works)? See `README.md` in the same folder as this file.

---

## Table of Contents

1. [What this app actually does](#1-what-this-app-actually-does)
2. [Before you start: two processes, one Sage 50 seat](#2-before-you-start-two-processes-one-sage-50-seat)
3. [The top bar](#3-the-top-bar)
4. [Manual Run tab](#4-manual-run-tab)
5. [Automatic Sync tab](#5-automatic-sync-tab)
6. [Customer Refresh tab](#6-customer-refresh-tab)
7. [Watermark tab](#7-watermark-tab)
8. [History & Logs tab](#8-history--logs-tab)
9. [PortPro tab](#9-portpro-tab)
10. [Sage 50 tab](#10-sage-50-tab)
11. [Settings tab](#11-settings-tab)
12. [About tab](#12-about-tab)
13. [Licensing & About tab](#13-licensing--about-tab)
14. [Understanding automatic gap-fill ("Finding the Gap")](#14-understanding-automatic-gap-fill-finding-the-gap)
15. [Working with more than one Sage 50 company file](#15-working-with-more-than-one-sage-50-company-file)
16. [Walkthroughs: common tasks step by step](#16-walkthroughs-common-tasks-step-by-step)
17. [Troubleshooting / FAQ](#17-troubleshooting--faq)
18. [Glossary](#18-glossary)

---

## 1. What this app actually does

The Admin app itself **never talks to PortPro or Sage 50 directly**. All it does is:

1. Read and edit two settings files (`appsettings.json` and `appsettings.Local.json`) that live in the **Service folder**.
2. When you click a Run/Start button, either launch `PortProSage.Service.exe` (the program that actually does the work) or drop a small request file for an already-running one to pick up.

So editing a field and NOT clicking that tab's **Save** button does nothing — and clicking Save does not, by itself, run anything either. The two actions are separate on purpose: you can change settings any time without accidentally kicking off a sync.

Every field in the app has a small circular blue **"?"** icon next to it — click it for a plain-language explanation and a worked example, right where you're looking. This guide covers the same ground in more depth, plus how the tabs work together.

At the very bottom of the window is a status bar that shows something like `Source: appsettings.json → Sync:PollingIntervalMinutes` whenever you click into a field — so you always know exactly which file, and which setting inside it, you're editing.

---

## 2. Before you start: two processes, one Sage 50 seat

There are two distinct ways to run a sync, and they are **mutually exclusive** — you can only ever have one active at a time:

| | Manual Run | Automatic Sync |
|---|---|---|
| You start it from | **Manual Run** tab | **Automatic Sync** tab |
| What actually launches | A one-shot process that runs once and exits | A long-running process that keeps polling |
| You choose | Exactly which invoices to process (by date, range, or list) | Nothing per-run — it always continues from the watermark |
| Runs until | It finishes (usually seconds to a few minutes) | You click Stop, or the machine restarts |

**Why they can't run together:** both would try to open the same Sage 50 company file under the same Sage 50 username at the same time. Sage 50 rejects a second simultaneous session under one username — so the app disables whichever button would create a conflict, and the top bar's **Process:** status line always tells you which one (if either) is currently active. The **Customer Refresh** tab (section 6) shares this same restriction — it's disabled whenever anything else is running, for the same reason.

If you need to run something manually while the Automatic Service is on, stop the Automatic Service first (top bar **Stop** button, or the Stop button on the Automatic Sync tab), do your manual run, then start it again.

---

## 3. The top bar

Visible above every tab, at all times:

- **Service folder** — the folder containing `appsettings.json`, `appsettings.Local.json`, and `PortProSage.Service.exe`. The app guesses this on first launch and remembers whatever you last pointed it at.
  - **Browse...** — pick a different folder (a full folder picker dialog).
  - **Reload** — re-read settings from whatever folder is currently typed in the box, without opening the picker. Use this if you edited the settings files by hand outside the app.
- **Target Sage50: `<path>`** — always visible, shown in bold below the Service folder row on every tab. This is the exact Sage 50 company file every write in the app will go to *right now*, using whatever's currently saved on the Sage 50 tab — not something you need to switch tabs to check. Reads **"Target Sage50: (no path specified yet - set it on the Sage 50 tab)"** until a path has ever been configured. See [section 15](#15-working-with-more-than-one-sage-50-company-file) for why this matters beyond just "which file" — it's also what splits Customer Refresh and History & Logs data apart per company file.
- **Help** — opens this User Guide (the `USER_GUIDE.html` version) in your default web browser. This file (`USER_GUIDE.md`) is the same content in plain text, kept as the editable source.
- **v2.15.2** (top-right, gray) — the exact build number of the Admin app you're currently running. Useful when confirming "did the new build actually install" — compare this against what you were told to expect.
- **Process:** — always shows the real, current state:
  - **Not running** (red) — nothing is active; either button is free to use.
  - **Automatic Service running - PID 1234, since 9:03 AM** (green).
  - **Manual Run running - PID 5678, since 9:15 AM** (orange).
  - A small spinning progress bar appears next to this label whenever anything is active.
- **Stop** (top bar) — stops whichever process is currently running, without needing to switch to the tab that started it. Confirms first.

---

## 4. Manual Run tab

Use this to run a sync **exactly once, right now**, with full control over exactly which invoices get processed.

### Mode dropdown — the three ways to pick invoices

As of 2026-08-25, "Continue (from where we left off)" and "Last changed date" have been removed from this dropdown — Continue was mechanically identical to the Automatic Service's own poll cycle (see [section 5](#5-automatic-sync-tab)'s "Continuous (Pass-1)"), so there was no real reason to duplicate it here, and Last changed date was rarely used. If you specifically need the Automatic Service's own watermark-driven continuation to run once, right now, by hand — start it from the Automatic Sync tab instead.

| Mode | Selects invoices by | When to use it |
|---|---|---|
| **Invoice date** (default) | The invoice's own PortPro billing date, within your From/To window | The safe default for "process everything invoiced in this window." Can't accidentally pull in something merely *edited* recently that's actually dated long ago. |
| **Invoice number range** | Reference numbers between a Start and an End (both ends included) | You know the numeric range of what's missing, e.g. "everything between RSRE_000090 and RSRE_000095." |
| **Invoice number list (comma-separated)** | An explicit, exact list of reference numbers you type in | You know precisely which invoice(s) you need, e.g. re-checking one specific invoice that failed earlier. This mode looks each one up individually rather than paging through PortPro's list — which is also why it's more reliable at finding an invoice the list view sometimes misses (see [section 14](#14-understanding-automatic-gap-fill-finding-the-gap)). |

None of these modes read or change the saved watermark by default — each is a one-time override for this run only. The one exception: **Invoice date** mode has its own checkbox, **"Watermark will be updated with this run's End Date"**, described next.

#### Watermark will be updated with this run's End Date

Only shown for Invoice date mode. **Checked by default.** When checked, this run also advances the Automatic Service's saved watermark from what it actually processed — the same thing the Automatic Service's own Pass-1 does, just without letting the watermark dictate the range (this run's From/To are used exactly as you entered them either way). Only the **date** half of the watermark ever moves this way, from each processed invoice's own real PortPro date — never the invoice-number half, and never simply this run's End date verbatim.

Uncheck it for a one-off range that shouldn't move the Automatic Service's position — leaving the watermark untouched just means the Automatic Service will later re-check that same window on its own and skip everything as already-imported, which is harmless but redundant.

Never saved — resets to its default every time this tab loads or Mode changes, so it can't silently carry forward into an unrelated later run.

#### Example — Invoice date

> **Goal:** Import everything invoiced in July 2026. Set Mode = **Invoice date**, From = `2026-07-01`, To = `2026-07-31`, then click **Manual Run**.

#### Example — Invoice number list

> **Goal:** Re-check three specific invoices that came back as failures last week. Set Mode = **Invoice number list (comma-separated)**, and type: `RSRE_000284, RSRE_000301, RSRE_000455` then click **Manual Run**.

### Other fields on this tab

- **Cutoff (Lower) Invoice Date** — a hard floor: no invoice dated before this date is *ever* processed, by Manual Run or the Automatic Service, no matter which Mode is used. This exists specifically to stop Sage 50's own "Do Not Allow Transactions Dated Before…" rule from rejecting an invoice mid-run and killing everything after it. This field is **shared** with the Automatic Sync tab (it's the exact same setting shown twice) and saves the instant you change it — you don't need to click a Save button for this one field.
- **Start invoice number / End invoice number** — only used by Invoice number range mode. Leave either blank for "no bound in that direction."
- **Invoice number list (comma-separated)** — only used by Invoice number list mode. This box resizes itself as you resize the window (it always tracks about 75% of the window's width), so a long list stays easy to read instead of being squeezed into a fixed-size box.
- **Override "Already Imported" check for this run** — normally, an invoice this app already recorded as imported is silently skipped on every later run, so it's never posted to Sage 50 twice. Checking this box turns that skip off *for this run only*: a previously-imported invoice is re-validated and re-posted instead.
  - **Never saved anywhere.** It always starts unchecked when the app opens, and resets itself back to unchecked the moment the run it applied to finishes or is stopped — it can never silently carry forward into an unrelated later run.
  - **Checking it shows an extra warning** before the run starts: *"The invoice number(s) from the selected Mode should already be removed from Sage 50 before running with 'Override Already Imported' checked - otherwise this WILL create duplicate invoices in Sage 50. Are you sure you want to proceed?"* This is not just a formality — if the invoice genuinely is still sitting in Sage 50, re-posting it creates a real duplicate transaction. Only use this after confirming, in Sage 50 itself, that the invoice(s) covered by the selected mode actually need to go in again.
  - **Any run that used it is marked "(Override)"** everywhere its Mode is shown — the History & Logs grid, the Previous Run section, and the Summary tab — so it's never ambiguous after the fact whether a run bypassed the dedup check.
- **Max invoices to process (0 = no limit)** — caps how many *genuinely processed* invoices this run will actually handle, on top of whatever Mode already selected. "Processed" means imported (or, under Dry Run, simulated as imported) or failed — an invoice already recorded as imported previously does **not** count against this cap, since nothing was actually done for it. Example: Max = 10 with 5 of the next invoices already imported and 10 genuinely new ones processes all 10 new ones (15 total looked at, not stopping at the 10th invoice overall).
- **Show command window while running** — checked (the default) pops up the Service's console window so you can watch it work live; unchecked runs it hidden in the background (you'd check progress via History & Logs instead). Also **shared** with the Automatic Sync tab, and also saves instantly.
- **Dry run (Simulated - Default 10 Invoices and no real Sage 50 Changes)** — the exact same setting as the **Dry run** checkbox on the Sage 50 tab, shown and editable here too — checking or unchecking it either place changes it everywhere, saved immediately, no separate Save button needed. When checked, nothing is actually written to Sage 50; the run's log and History & Logs just show what it *would* have done.
  - **Checking it also sets Max invoices to process to 10 automatically** — a full-range Dry Run isn't usually necessary just to sanity-check behavior, so a test run stays quick by default. **Unchecking it resets Max invoices back to 0** (no limit), so a leftover test cap can't silently limit a real run afterward. Change Max invoices by hand after toggling if you need a different number for that particular test.
  - A Dry Run invoice is never marked as imported, so running the exact same range for real afterward genuinely processes it, not skips it as already done.
  - **Any run that used Dry Run is marked "(Dry Run)"** everywhere its Mode is shown — the History & Logs grid, the Previous Run section, the Summary tab, and the completion pop-up — and the "Imported" count is relabeled "Imported (SIMULATED...)" so it's never mistaken for a real write.

### Previous Run (read-only)

A snapshot of the most recently completed run — whether it was started from here, from Automatic Sync, or from a trigger file — so you can always see at a glance what actually happened last, without digging into History & Logs. Shows Mode (with "(Override)" appended if that run used the override checkbox above), date/invoice range, Max invoices, the first and last invoice actually processed, a SUCCESS / FINISHED WITH ERRORS / INTERRUPTED result line, and (for an Invoice number list or gap-fill run) the exact invoice list that was used.

Every field here has its own **Copy** button (always available, even if the field is empty) — click it to copy that exact value to the clipboard. The most useful one is **Invoice List Used**: copy it and paste straight into the **Invoice number list** field above to re-run the same set.

**A successfully-completed Invoice number list clears itself automatically.** If every invoice in the list was found and processed with no failures at all, the Invoice number list field is emptied once the run finishes — so a list you've already dealt with can't be accidentally re-submitted later. If anything in the list wasn't found or failed, the list is left as-is so you can see and fix it.

### Buttons

- **Manual Run** — validates your inputs first (catches things like an End date before the From date, or an empty invoice list), warns you if Sage 50 already appears to be open under a possibly-conflicting session, shows you a confirmation dialog summarizing exactly what's about to run (write mode, company file, mode, range, cap), and only then actually starts it. Automatically switches you to the History & Logs tab and highlights the new run.
- **Stop Manual Run** — only enabled while a manual run is active. Sends a graceful shutdown signal first (so anything already imported, and the watermark, stay correctly recorded up to that point) and only force-kills the process if it doesn't respond within 5 seconds.
- **Save** — remembers your current field values (Mode, dates, invoice numbers, Max) so they're already filled in next time you open the app. This also happens automatically the moment you click Manual Run, so you rarely need to click Save by itself.

---

## 5. Automatic Sync tab

Use this to configure and control the background service that runs continuously, watching for new/changed invoices on its own schedule.

### The two active passes (and a third, deliberately not built yet)

As of 2026-08-26, the Automatic Service's own cycle works in passes:

- **Continuous (Pass-1)** — the main poll. Watermark-driven, based on the invoice's own **creation/completed date** (the same basis Manual Run's "Invoice date" mode uses) — **not** PortPro's "last changed" timestamp, which it used before this date. Every cycle continues from the saved watermark, processes whatever's newly eligible, then advances the watermark to match.
- **Find Gaps (Pass-2)** — the existing automatic gap-fill sweep (see [section 14](#14-understanding-automatic-gap-fill-finding-the-gap)), unchanged — it runs after Pass-1 (and after every other range-based run, manual or automatic) regardless of which basis found the range.
- **InvDate Changed (Pass-3)** — **not implemented.** This would be a third pass finding invoices by PortPro's "last changed" date within a range — catching an invoice that was *edited* after its own creation date. Deliberately deferred: Sage 50's SDK has no way to push an update to an already-posted invoice today (`Sage50Client` can only create a new invoice, never modify an existing one), so finding a changed invoice would have nothing useful to do with that information yet. If this is ever built, the right mechanism is **Reverse + Insert** (reverse the original posted invoice through Sage 50's own accounting reversal, then post a corrected one) — not a hard delete, which Sage 50 doesn't support for a posted transaction and which would break the audit trail even if it did. This needs its own thorough design and testing before it's part of any version.

**Known, accepted limitation of this design:** an invoice edited in PortPro *after* its own creation date will **not** be automatically re-synced once its creation date has scrolled past the current watermark — Pass-1 only ever looks at creation date, never at what changed later, and Pass-3 (the only pass that would catch this) isn't built. If an invoice's data changes in PortPro after it was already posted to Sage 50, catching that requires a **manual** re-check — a Manual Run (Invoice date mode) covering that invoice's actual date, or its own invoice number — there's no automatic path for it in this version.

### Watermark Invoice Date

The saved "continue from" position Pass-1 (and Manual Run's own watermark-advance checkbox — [section 4](#4-manual-run-tab)) reads and moves forward. One editable field (date + time) — pick a value and click **Save Automatic Sync settings** below to set it explicitly; no separate Save button and no checkbox to clear it, since the watermark is always a concrete value.

- **Defaults to 6 months back** if nothing has ever synced yet (same default the Cutoff Date field uses), instead of showing an empty/cleared state.
- **Must not be earlier than the Cutoff (Lower) Invoice Date** below it — saving a watermark before the cutoff is rejected with an error, since the cutoff already guarantees nothing before it is ever processed anyway.
- **Can move backward, not just forward** — unlike normal sync progress, which can only ever advance it. Moving it back causes invoices in the newly-covered range to be re-fetched and re-checked on the next run; already-imported invoices are tracked separately (by PortPro invoice id, not by date) and will **not** be double-posted — only genuinely missed ones actually import.
- **Disabled while the Automatic Service or a Manual Run is active** — editing it mid-run risks the edit being silently overwritten the moment that run next advances this same value. Re-enables once nothing is running. While disabled, the displayed value refreshes live every few seconds, so it always reflects the true current position by the time it becomes editable again — confirmed live 2026-08-26 that without this, saving any unrelated setting after a long Automatic Service session could silently regress the watermark back to a stale snapshot.
- **Refresh** button reloads the live value from the database, discarding any unsaved edit — useful for watching it advance in near-real-time while it's disabled during a run.
- Scoped per Sage 50 path, same as everything else described in [section 15](#15-working-with-more-than-one-sage-50-company-file).

### Fields

- **Automatic Sync - Processing Delay (Days)** — holds back the most recent N days before they're eligible to sync. A live readout next to the field ("Upper cutoff date: today − 7 day(s)") shows exactly what date that currently resolves to. This is a rolling delay, not a permanent skip — a held-back invoice simply becomes eligible once it's old enough. Set to 0 to disable the delay entirely.
- **Automatic Sync - Polling Interval (minutes)** — how often the service checks PortPro for changed invoices on its own. (Manual requests dropped into the trigger folder are always picked up within about 15 seconds, regardless of this setting.)
- **Cutoff (Lower) Invoice Date** and **Show command window while running** — the same two shared fields described under Manual Run above; changing either here changes it everywhere, and both save instantly.

### Previous Run

Identical in content and layout to the Manual Run tab's Previous Run section — it's genuinely the same underlying data, just also shown here so you don't need to switch tabs to check it. Excludes "Find Gaps (Pass-2)" runs, which would otherwise always be "the most recent run" a moment after Pass-1 finishes, burying what Pass-1 actually did.

### Buttons (bottom of the tab, matching Manual Run's layout)

- **Start Automatic Service** — same pre-flight checks as Manual Run (nothing else running, Sage 50 not already open elsewhere), shows a confirmation summarizing write mode, company file, polling interval, and the current watermark, then starts the long-running background process.
- **Stop Automatic Service** — confirms, then gracefully stops it (falling back to a forced stop only if it doesn't respond).
- **Save Automatic Sync settings** — saves the Polling Interval, Processing Delay, **and the Watermark Invoice Date** (validated against the Cutoff Date first) together in one action.

### Important: settings changes need a restart

If the Automatic Service is already running and you change/Save a setting anywhere in the app (polling interval, cutoff date, account mappings, anything), **the running process keeps using the old values** until you stop and restart it. The app reminds you of this after every Save.

---

## 6. Customer Refresh tab

Its own top-level tab, right after Automatic Sync, for one specific job: comparing **every** PortPro customer against Sage 50 side by side, and optionally pushing chosen ones (new customer creations, or full-profile overwrites of existing ones) into Sage 50 — on demand, only for the rows you actually pick.

This is different from the automatic "Update Customer with latest changes in PortPro" sweep (Sage 50 tab), which only ever touches a customer whose PortPro profile has changed since it was last synced, in the background, and never creates a new customer. This tab ignores that changed-since-last-sync check entirely, compares *every* customer every time you extract, and **can create a brand-new Sage 50 customer** for a customer with no match (gated by the same "Auto-create missing customers" setting used when a new customer shows up on an invoice).

**⚠ Running an UPDATE overwrites Sage 50 customer data.** Whatever PortPro currently has on file replaces whatever's in Sage 50 for that customer — any change made directly in Sage 50 to a customer's address, phone, email, etc. is lost for any row you check and run.

### The two-step workflow

Nothing loads automatically — the grid starts **empty every time the app opens**, showing a placeholder message ("Press \"Extract All Customer\" to pull PortPro customers and their Sage 50 comparison.") until you deliberately pull data.

1. **Extract All Customer** — fetches every PortPro customer (the full account, not a partial page — see the note below) and checks each one against Sage 50 by name. Entirely read-only; nothing is written. Fills the grid with one row per customer, each marked **INSERT** (no match found in Sage 50) or **UPDATE** (a match exists), showing PortPro's incoming profile and, for an UPDATE row, Sage 50's current profile side by side. Pressing it again re-extracts and replaces the whole list. A label above the grid shows when it was last extracted and a running total, e.g. *"Last extracted: 14:22:10 - 223 customer(s) total (6 to insert, 217 to update)."*
2. **Run Selected** — tick the rows you actually want processed (or use **Select all**, or press the **spacebar** while a row has focus to toggle just that row — works from any column, not only the checkbox itself), then click this button. This is the only step that writes anything, and only for the rows you checked.

> **The 50-record pagination bug is fixed.** PortPro's `/customer` endpoint used to silently cap every response at 50 records no matter what page size was requested, so earlier builds of this feature only ever saw the first 50 customers. This is now fixed — Extract All Customer genuinely pulls every customer on the account (confirmed against a live account with 223 real customers).

### The grid

Columns, left to right: **Select** (checkbox) · **# (Seq)** — the row's position in the extract, so "223 customers total" and "row 223" line up · **CustomerName** · **PortProDetails** · **Operation** (INSERT/UPDATE) · **Applied** · **Date** · **SageCustomerName** · **SageDetails**.

- **An INSERT row is shown in red font**, from the Operation column through the last column, so a genuinely-new customer is impossible to miss while scanning a long list.
- **Applied / Date** show the result of the *last time this customer was actually run* through Run Selected — a green/red-style success flag plus the exact date/time — and they're pre-filled from a persisted record the moment you extract, even across app restarts or a brand-new Extract. **The grid does not clear or reset after Run Selected finishes** — it updates the rows you ran in place with their fresh Applied/Date result, so you can see exactly what just happened without losing the rest of the list. A Dry Run's result is deliberately **never** persisted here (see below), so Applied/Date only ever reflects real writes.
- **Wrapped Details** checkbox — toggles word-wrap on the PortProDetails/SageDetails columns. Unchecked (the default) keeps every row a single compact line; checked wraps long text and grows each row to fit it.
- **Search box** (top-right) — type a PortPro customer name to scroll to and highlight the first match. This does not filter or hide any row, just jumps to and selects the match.

### Dry run (Simulated - no real Sage 50 Changes)

A **separate, independent** Dry Run — this is **not** the same setting as the Dry run checkbox on Manual Run / Sage 50 tab, and toggling one never affects the other.

- **Defaults to unchecked every time the app opens** — a real write is the default action here, unlike Manual Run's own Dry Run. Resets itself back to unchecked after every Run Selected, success or failure, so a test toggle can't silently carry forward into a later real run.
- There is no Max-customers cap tied to it (unlike the old design) — Run Selected always processes exactly the rows you checked, nothing more.
- **A Dry Run result is never saved to the Applied/Date columns' persisted history** — it's a pure simulation that leaves no trace in the tracked "last real outcome" for that customer.
- The confirmation dialog and its title both lead with the write mode in capital letters — **"\*\*\* DRY RUN - simulated only, nothing will actually be written to Sage 50. \*\*\*"** or **"\*\*\* REAL WRITE - this will make real changes to Sage 50. \*\*\*"** — so which one is about to happen is the very first thing you see, not something buried further down the dialog.

### Viewing data for: (path picker)

A dropdown at the top of the tab lists every Sage 50 company-file path this app has ever recorded Customer Refresh results against.

- Selecting the **currently-configured** path (matching the "Target Sage50" banner in the top bar) is **live mode** — Extract and Run Selected work exactly as described above.
- Selecting **any other, past** path switches the tab to a **read-only historical view** of that path's last-known results — Extract and Run Selected are both disabled, since a real PortPro-vs-Sage50 comparison is only possible against whichever company file is actually connected right now. This is how you can look back at what a *different* Sage 50 file's Customer Refresh history looked like without needing to switch the app over to it.

See [section 15](#15-working-with-more-than-one-sage-50-company-file) for the full explanation of why this dropdown exists and how it splits data between company files.

### No pre-flight "Sage 50 already open" warning

Unlike Manual Run and Automatic Sync, this tab does **not** show a "Sage 50 might already be open under this username, continue anyway?" warning before starting. If Sage 50 genuinely is already open under the same username, the run simply fails immediately with a clear error explaining why — there's no extra confirmation step to click through first.

Both Extract and Run Selected are disabled while the Automatic Service or a Manual Run is active (all of them connect to Sage 50 under the same account, and Sage 50 rejects a second simultaneous session), and each one registers its own row in History & Logs once finished ("Customer Refresh (scan)" for an Extract, "Customer Refresh" for a Run Selected) — check there for exactly what was found, created, updated, or failed.

---

## 7. Watermark tab

**This tab was removed 2026-08-26.** The watermark — the saved "continue from" position Pass-1 and Manual Run's watermark-advance checkbox both use — now lives as a single editable field at the top of **[section 5, the Automatic Sync tab](#5-automatic-sync-tab)**, the only tab that actually consumes it. See that section for the full, current description (no more separate Invoice # field, no checkbox, saved via "Save Automatic Sync settings," and disabled while a run is active).

---

## 8. History & Logs tab

Your record of every run that's ever happened — automatic, manual, Customer Refresh, or from a trigger file — with full drill-down detail.

### Viewing data for: (path picker)

Right below the Refresh button, a **Sage50 path** dropdown lists every Sage 50 company-file path this app has ever recorded a run against. Selecting a path filters the grid to just that path's runs. Entries recorded **before** this feature existed (or before a run's own result was written by a version of the Service that included it) don't have a known path — those always keep showing, regardless of which path is selected, so older history is never silently hidden. See [section 15](#15-working-with-more-than-one-sage-50-company-file) for the full picture.

### The grid

Columns, left to right:

| Column | Meaning |
|---|---|
| **Select** | A checkbox for multi-row delete — see [Deleting a run](#deleting-a-run-or-runs) below. Always starts unchecked. Press the **spacebar** while any cell in a row has focus to toggle that row's checkbox without needing to click the narrow checkbox column itself. |
| **#** | A short, stable reference number for the run (assigned in the order it happened; never renumbers as new runs are added — easier to say/type than the full Request ID). |
| **Request ID** | The full internal ID for this run. |
| **Source** | "Automatic Service", "Manual Run", "Customer Refresh", or "Trigger file". |
| **Mode** | Which selection mode was used (see [section 4](#4-manual-run-tab)); the Automatic Service's own poll shows as **"Continuous (Pass-1)"** (see [section 5](#5-automatic-sync-tab)); a gap-fill run shows as **"Find Gaps (Pass-2) (found/checked)"** once finished (see [section 14](#14-understanding-automatic-gap-fill-finding-the-gap)); a run with the "Override Already Imported" checkbox shows **"(Override)"** appended; a Dry Run shows **"(Dry Run)"** appended. |
| **Process Start / Process End** | When the run's process actually started and finished. |
| **Inv Start Date / Inv End Date** | The actual invoice-date window this run covered. |
| **Fetched** | How many invoices PortPro returned for this run (shown as `found/checked` for a gap-fill run — see section 14). |
| **Imported** | How many were actually written to Sage 50. |
| **Skipped** | How many were skipped because they were already imported previously. |
| **Not found** | How many candidates this run specifically looked up one at a time and got a "doesn't exist" answer for. Only ever non-zero for an Invoice number list run or a gap-fill run — the other modes don't check individual candidates this way. |
| **Zero/-ve Amt** | Skipped because the invoice total was zero or negative (nothing to post). |
| **Before cutoff** | Skipped because the invoice was dated before your Cutoff (Lower) Invoice Date. |
| **Failed validation** | Failed a pre-import check (e.g. unmatched account) before ever reaching Sage 50. |
| **Failed write** | Passed validation but the actual write to Sage 50 failed. |
| **Status** | Completed / Running / Interrupted (partial) / Interrupted (no result) / Skipped / Pending (queued). |
| **Delete** | A button that deletes just this one row — see [Deleting a run](#deleting-a-run-or-runs) below. |

Click **Refresh** to reload from disk (a "Last refreshed: HH:mm:ss" label next to it confirms when). The grid does *not* auto-refresh every couple of seconds anymore while something is running — only when you click Refresh, or once a run genuinely finishes — specifically to stop the list from flickering while you're trying to read it.

### Deleting a run (or runs)

Two ways to delete, both requiring confirmation first:

- **One row** — click that row's **Delete** button. Confirms, showing the Request ID, Mode, and start time of what you're about to remove.
- **Several rows at once** — tick the **Select** checkbox on each row you want gone (tick it, click **Select all** top-right of the grid, or press spacebar while a row has focus), then click **Delete Selected** (also top-right, in red). Confirms once for the whole batch.

A run that's **still actively in progress can't be deleted** — you'll see a warning telling you to stop it first, either for that one row or (for a batch that includes one) a note saying how many were skipped while the rest are deleted normally.

Deleting a run is permanent and removes everything it produced:

- Its own request/result files.
- Any failed-transaction CSV report it generated.
- Its rows in the local tracking database — so those specific invoices are treated as brand new again the next time a real run touches them, exactly as if this run had never happened. **This is scoped to the exact Sage 50 path that run actually used** — deleting a run against one company file never removes tracking rows that genuinely belong to a different path, even if they happen to share the same invoice reference number. (This is different from the Settings tab's "Clear All Imported-Invoice Records," which wipes *everything*, not just one run's invoices.)

A confirmation dialog appears afterward too, listing exactly how many files, failed-transaction reports, and tracking rows were actually removed.

### The detail tabs (for whichever row you've selected)

- **Summary** — a full plain-text readout of everything about the run: request details (including a clear note if "Override Already Imported" was on), the exact range/list used, every count (including the same "found/checked" framing as the grid), duration, and the watermark before/after. If this run created or updated any customers in Sage 50 (either a customer auto-created because an invoice needed one, or an existing one kept in sync by the "Update Customer with latest changes in PortPro" setting), a **Customers created in Sage 50** / **Customers updated in Sage 50** line shows the counts — omitted entirely when both are zero, so an ordinary run that touched no customers doesn't get extra clutter.
- **Validate Invoice Extracted** — one row per invoice this run touched: **PortPro Customer Name**, invoice #, PortPro date, success/fail, the resulting Sage 50 invoice number, and any messages (e.g. why it failed) — wrapped automatically if a message is too long to fit. A gap-fill candidate PortPro confirmed doesn't exist shows `Success: No` with a message like `RSRE_003947 (Invoice from identified GAP, not found in PortPro)`; an invoice you typed into Invoice number list yourself that wasn't found reads `(not found in PortPro)` instead, without the "identified GAP" wording.
- **Invoice Transferred** — one row per invoice that was actually written to Sage 50: **PortPro Customer Name**, PortPro #/date, **Sage50 Customer**, Sage 50 #/date, due date, total amount, tax charged. **Sage50 Customer** reads `CREATED` if that invoice's customer didn't exist in Sage 50 and was just auto-created, `UPDATED` if the customer already existed and "Update Customer with latest changes in PortPro" (Sage 50 tab) is on, or blank otherwise. `UPDATED` means "kept in sync by that setting," not necessarily "changed at the exact moment this invoice posted" — the actual profile sync runs once per whole run, not once per invoice.
- **Warnings / Validation** — just the warning/validation lines from this run's log, filtered out of the noise.
- **Failed Transactions** — just the error/failure lines.
- **Full log** — the complete raw log text for this run's time window, with a search box that filters as you type.

### Reading a gap-fill row

A "Find Gaps (Pass-2)" row is automatically created after almost every other run (see [section 14](#14-understanding-automatic-gap-fill-finding-the-gap)) — it's the app double-checking the range it just covered. If you see one with, say, "Fetched: 0/79" and "Not found: 79", that means it checked 79 candidate invoice numbers individually and genuinely didn't find any of them — that's a normal, healthy result (gaps in invoice numbering are completely ordinary), not an error.

---

## 9. PortPro tab

Connection settings for PortPro's API. You'll rarely need to touch most of these after initial setup:

- **Base URL** / **Invoice endpoint** — where PortPro's API lives and where invoice data comes from. Only changes if PortPro moves their API.
- **Customer endpoint** — the path used to fetch PortPro's *full* customer profile (address, billing email, contact, payment terms) — a separate, richer object than the lightweight caller info embedded on each invoice. Used when auto-creating a new Sage 50 customer, by the periodic customer-update sync (see the Sage 50 tab's "Update Customer with latest changes in PortPro"), and by the Customer Refresh tab's Extract All Customer.
- **Access token endpoint** — kept for reference; the real login flow below uses **New token endpoint** instead.
- **New token endpoint** — where a fresh access token is requested using the Refresh token, automatically, whenever the current one expires. You never need to trigger this by hand.
- **Page size** — how many invoices PortPro returns per page (the app transparently pages through everything, this just controls the page size).
- **Timeout (seconds)** — how long to wait for PortPro to respond before giving up on that request.
- **Access token** / **Refresh token** (secrets) — your PortPro API credentials. The access token refreshes itself automatically using the refresh token; you'd only ever hand-enter these once, during initial setup, using the real values from PortPro's own integration settings screen. Use **Test Connection** to confirm the currently **saved** credentials actually work (not unsaved edits — Save first, then Test).

Click **Save PortPro settings** to persist changes.

---

## 10. Sage 50 tab

Connection credentials and the account-mapping rules that decide exactly where each invoice line posts in Sage 50. This is the most consequential tab in the app — take the Dry run switch seriously.

- **App name** / **App ID** — how this app identifies itself to the Sage 50 SDK. Set once during setup, rarely changed.
- **Company data path** — the full path to your `.SAI` company file. An editable dropdown: type or paste a path directly, pick one you've used before from the list, or click **Browse...** to find the `.SAI` file on disk. The dropdown lists every path you've ever saved here, plus every path this app has real activity against in its own tracking, so a path never goes missing from the list just because it predates this feature. Use **Test Connection** to confirm the app can actually open it (see below). **Changing this and saving effectively switches the app to a different Sage 50 file** — see [section 15](#15-working-with-more-than-one-sage-50-company-file) for exactly what does and doesn't carry over when you do.
  - **Picking a different path from the dropdown restores that path's own saved configuration** — username, password, App name/ID, account defaults, tax codes, and the charge account map all switch to whatever was last saved for that specific path, since a different company file can genuinely need different Sage 50 credentials or a completely different chart of accounts. Typing a brand-new path leaves everything else exactly as it is. The top bar's **Target Sage50** banner updates the instant you pick a path, even before you click Save — it's just showing what you selected, not yet what's actually active until you save.
- **Sage50 User Name** / **Password** — must be a **dedicated account**, never one a person also logs into interactively, since Sage 50 rejects a second simultaneous session under the same username.
- **Expected SDK version** — optional; if set, logs a warning if the installed SDK's version doesn't match. Leave blank to skip the check.
- **Default revenue account** — the GL account used for a charge that has no specific mapping below (see Charge account map). If this is also blank, an unmapped charge causes that invoice to fail outright rather than posting somewhere undefined.
- **Ignore account 1-on-1 match and apply default** — controls what happens when a charge's resolved account (from the Charge account map below, or Default revenue account if unmapped) can't be confirmed to exist in Sage 50. **Unchecked** (normal): that invoice fails right there and the run stops on it — nothing gets posted. **Checked**: the run falls back once to the Default revenue account instead and posts there, leaving a warning to review later. Either way, the Charge account map is the hard, authoritative source for which account a charge is *supposed* to use — this checkbox never changes that mapping, only what happens if Sage 50 can't confirm the resolved account still exists; and if the Default revenue account itself can't be confirmed, that always stops the run regardless of this setting. Checked effectively runs with a safety net (every charge posts somewhere); unchecked is the strict setting (nothing posts to an unconfirmed account).
- **Default receivable account** — currently has **no effect**. Sage 50's customer object has no per-customer receivable-account property to write it to — Simply Accounting/Sage 50 posts every customer to one global AR control account, configured once in Sage 50 itself (Setup ▸ Settings ▸ Customers & Sales ▸ Linked Accounts), not per customer through this integration. Kept in case a future Sage 50 SDK version adds support.
- **Default net payment terms (days)** — the fallback "Net N days" term applied to an invoice when PortPro's own real per-invoice terms can't be used (missing, or in a unit other than days). PortPro normally supplies real per-invoice terms directly, so this is a safety net, not the primary source. Example: 30.
- **Accounts To Trust (comma-separated)** — a workaround for a known Sage 50 SDK quirk where a real, existing account is sometimes wrongly reported as "does not exist." If a run fails with that specific error for an account you've manually confirmed *is* real in Sage 50, add its number here (comma-separated with any others). **Do not** add an account here that's genuinely missing — fix the actual setup instead; this field is only for confirmed-real-but-misreported accounts.
- **Auto-create missing customers** — checked: an unrecognized PortPro customer is created automatically before posting, using PortPro's full customer profile (address, email, contact, currency) where available. Unchecked: that invoice fails validation instead ("customer not found"). Also gates whether an INSERT row on the Customer Refresh tab is actually allowed to create a new customer.
- **Update Customer with latest changes in PortPro** — default checked. Once per Automatic Service cycle and once per Manual Run, checks every PortPro customer for a profile change since it was last synced, and pushes any change into the matching *existing* Sage 50 customer automatically. Does not affect creating brand new customers, which always happens regardless of this setting. ⚠ PortPro always wins: a manual correction made directly in Sage 50 for one of these fields is overwritten the next time that customer's PortPro record changes.
- **Auto-create missing items/services** — the same idea, for charge/item lines.
- **Dry run (simulate writes - no real Sage 50 changes)** — **the most important switch on this screen.** Checked: nothing is actually written to Sage 50 — the run logs exactly what it *would* do instead. Always test a change (a new date range, a new account mapping, anything unfamiliar) with this checked first, confirm the log looks right, then uncheck it for the real run. Also editable directly from the Manual Run tab (the exact same setting, shown in both places — see [section 4](#4-manual-run-tab)) — toggling it either place saves immediately and takes effect everywhere. (This is **not** the same flag as the Customer Refresh tab's own Dry Run — see [section 6](#6-customer-refresh-tab).)
- **Tax codes** grid — maps a Canadian tax abbreviation found in a PortPro charge name (HST/GST/PST/QST) to the matching Sage 50 tax code (from Sage 50's own Setup ▸ Settings ▸ Company ▸ Sales Taxes ▸ Tax Codes screen). A recognized tax charge is **not** posted as its own line — Sage 50 applies the tax code directly to the revenue lines instead.
- **Charge account map** grid — maps each PortPro charge name (e.g. "PICK UP & DELIVERY", "FUEL SURCHARGE 1") to the Sage 50 GL account it should post to. Matched case-insensitively against each invoice line. Only the **Sage 50 Account Number** column actually affects posting — the glCode and account name columns are reference/audit only. A charge with a blank account number here falls back to Default revenue account.

Click **Save Sage 50 settings** to persist changes. **Test Connection saves this tab automatically first**, then attempts a real connect using exactly what's currently in the fields — you don't need to click Save separately before testing.

---

## 11. Settings tab

Everything else: email notifications, where files live, log cleanup, and a destructive reset utility.

### Email (failed-transaction reports)

- **Enabled** — master switch. When unchecked, a failed-transaction CSV is still saved to disk on every run with a failure, but no email is sent, and every field below is ignored.
- **SMTP host / port / Use SSL** — your outgoing mail server settings.
- **From address / Username / Password** — the sending account. Some providers (Gmail, Microsoft 365) require an app-specific password here, not your normal login password.
- **Recipients (comma-separated)** — everyone who should get a report whenever a run has at least one failure, e.g. `ashwani@smallarc.com, accounting@rushtransfer.com`.

### Folder Locations

Each has an **Open** button to jump straight to it in File Explorer:

- **Trigger folder** — where new manual/trigger requests are dropped for the Service to pick up. Also where a small `deleted-history-ids.json` file is kept (see [Deleting a run](#deleting-a-run-or-runs)) — you won't normally need to touch it directly.
- **Processed trigger folder** — where a request moves once handled; this is what History & Logs actually reads from.
- **State database path** — the single file tracking already-imported invoices, customer sync state, Customer Refresh results, and the watermark **for every Sage 50 path you've ever used** (see [section 15](#15-working-with-more-than-one-sage-50-company-file)) — it's one shared file, not one per path. **Never point two different client installs at the same file.**
- **Log folder** — daily rolling log files.
- **Failed transactions folder** — CSV reports, one per run that had a failure, regardless of whether email is enabled.
- **Minimum log level** — Information is the normal, recommended setting; switch to Debug only while actively troubleshooting something (it's much noisier).
- **Cleanup log after execution (Days)** — old log files past this many days are deleted automatically at the end of every run. This is **permanent** — set to 0 to disable cleanup entirely. **Apply Now** triggers the cleanup immediately using whatever value is currently saved (Save first if you just changed the number).

### Reset Imported-Invoice Tracking

- **Currently tracked as already imported** — a live count of how many invoices this app believes it has already posted **to the currently-configured Sage 50 path**, specifically (this is how it decides to silently skip something as "already imported" without re-checking Sage 50 itself every time). Switching to a different company file on the Sage 50 tab shows that path's own separate count, not a leftover from whichever file was configured before.
- **Clear All Imported-Invoice Records** — ⚠️ **irreversible.** Wipes this app's entire memory of what's already been imported **for the currently-configured path only** — it does not touch any other Sage 50 path's tracking, and does not touch Sage 50 itself in any way. After clearing, every invoice for that path looks brand new on the next run. This is only safe to do if you're certain the current Sage 50 company file genuinely doesn't already contain those invoices — otherwise they will be **posted again as duplicates**. Blocked entirely while any run is active.
  - If you only need to clear tracking for **one specific run**'s invoices, deleting that run from History & Logs does that (see [Deleting a run](#deleting-a-run-or-runs)) without wiping everything else.

Click **Save Settings** to persist the non-secret fields above (Email password saves too, just to the separate secrets file).

---

## 12. About tab

Static reference info: app version, SmallArc Inc. contact details (phone numbers for US/Canada, `contact@smallarc.com`, with a one-click Copy button), and a note about SmallArc's other product, Fixyee (`www.fixyee.com`). No functional controls here — just company/licensing information.

---

## 13. Licensing & About tab

A newer, more detailed license-and-contact screen, kept as its own separate tab alongside the classic About tab above rather than replacing it. It shows a License Information card (Licensed To, Location, License Type, Product, Version, and a masked License ID with a Copy button), Company and Support side by side, an Other Products section, and the same Authorized Use notice as the About tab.

> **Currently a design preview.** There is no licensing server behind this yet, so the license details shown (Licensed To, Location, License Type, License ID) are placeholder values, not read from a real license file. Treat this tab as a preview of what's coming, not as your actual license record — for that, the Authorized Use notice on either this tab or the About tab is the real, current statement of your license terms.

---

## 14. Understanding automatic gap-fill ("Finding the Gap")

Now labeled **"Find Gaps (Pass-2)"** in the app itself (the section title above keeps its original wording so existing links to it still work). This is the one behavior in the app that isn't a button you click — it just happens, automatically, after almost every run. It's worth understanding so a "Find Gaps (Pass-2)" row in History & Logs doesn't look like a mystery.

### Why it exists

PortPro's normal invoice list — the one every date-range or number-range run uses — can, under certain conditions, silently leave out real invoices that genuinely exist. Looking that invoice up *individually*, by its exact reference number, finds it fine; it's specifically the bulk list view that can miss it. Since there's no reliable way to know in advance whether a given run hit this, the app doesn't rely on you to remember to check — it checks automatically, every time.

### How it works

After any range-based run finishes (Manual Run or the Automatic Service, any Mode), the app automatically looks at the exact invoice-number range that run actually touched (from the lowest to the highest invoice number it saw), figures out which numbers in that range are *not* already recorded as imported, and looks each one up individually — the same reliable one-at-a-time lookup used by Invoice number list mode.

This shows up in History & Logs as its **own separate row**, one level below the run that triggered it, labeled **"Find Gaps (Pass-2)"**. You never select this yourself — there's no dropdown option for it.

### Reading the result

Once a gap-fill row finishes, its Mode column shows something like:

```
Find Gaps (Pass-2) (19/98)
```

That means: 98 candidate invoice numbers in the range were checked one by one, and 19 of them turned out to be real invoices that genuinely existed (and have now been imported). The other 79 were checked and confirmed to simply not exist — which is completely normal; invoice numbering in any system has gaps (voided invoices, numbers reserved and never used, etc.).

The same 19/98-style breakdown appears in the grid's **Fetched** and **Not found** columns for that row, and in full detail on the **Summary** tab if you select that row.

**A gap-fill row with "0 found" is not a failure** — it means the sweep found no genuinely missing invoices in that range, which is the expected, healthy outcome most of the time.

---

## 15. Working with more than one Sage 50 company file

If you only ever point this app at one Sage 50 company file, you can skip this section — everything just works the way the rest of this guide describes. This section matters the moment you switch between two or more `.SAI` files (e.g. a DEV/test company file and your real PROD one), because a lot of what the app remembers is now kept **separately for each one**.

### What's scoped per path

Every one of these is tracked independently for each distinct Sage 50 company-file path (the exact value of **Company data path** on the Sage 50 tab):

- **Already-imported invoice tracking** — the "don't post this again" memory used by every sync mode. Switching to a different path starts this fresh for that path; switching back to a path you used before brings its own tracking back exactly as you left it.
- **The watermark** ([section 5](#5-automatic-sync-tab)) — Pass-1 resumes from whichever path's own watermark is currently active.
- **Customer Refresh results** ([section 6](#6-customer-refresh-tab)) — the persisted Applied/Date history shown in the grid, and what the path picker shows in historical mode.
- **History & Logs' path filter** ([section 8](#8-history--logs-tab)) — which runs a given path's dropdown selection shows.

### What this means in practice

Say you're actively testing against a DEV company file, then switch **Company data path** on the Sage 50 tab to your real PROD file and save:

- The **Target Sage50** banner (top bar, [section 3](#3-the-top-bar)) immediately reflects the new path.
- History & Logs and Customer Refresh's path dropdowns will show PROD as a new (or already-known, if you've used it before) option — selecting it shows PROD's own history, not DEV's.
- The **skip logic** (already-imported invoices, customer sync state) automatically starts following PROD's own tracking. If PROD is genuinely new to this app, everything on it looks fresh; if you've synced to PROD before, its own prior tracking picks back up right where it left off — DEV's test data never leaks into it, and vice versa.
- **Interestingly, this also means the skip logic you exercised while testing on DEV doesn't apply to PROD at all** — a DEV test run doesn't make PROD think anything's already imported. Each path's memory is entirely its own.

### The path picker dropdowns

Both Customer Refresh and History & Logs show a **"Viewing data for:"** / **"Sage50 path"** dropdown, listing every path the state database has ever recorded anything against.

- It's a live list — a brand-new path shows up the moment you save it on the Sage 50 tab and revisit either tab, without needing to already have data for it.
- Selecting the path that matches the current "Target Sage50" banner keeps a tab fully live (Extract/Run Selected work normally on Customer Refresh; History & Logs just filters to that path).
- Selecting a different, past path switches Customer Refresh to a read-only historical view (see [section 6](#6-customer-refresh-tab)) and filters History & Logs to that path's own runs.
- Before any path has ever been configured, both dropdowns show **"(Sage50 path not defined)"**.

---

## 16. Walkthroughs: common tasks step by step

### Run a normal catch-up sync manually, right now

There's no standalone "run Pass-1 once" button — Pass-1 is the Automatic Service's own continuous poll ([section 5](#5-automatic-sync-tab)). For a genuine one-off catch-up from Manual Run instead:

1. Go to **Manual Run**.
2. Set Mode to **Invoice date**, and set **Invoice Date From/To** to cover the gap you want caught up.
3. Leave **"Watermark will be updated with this run's End Date"** checked (the default) so this run also advances the watermark, keeping Pass-1 in sync with what you just covered.
4. Click **Manual Run**, confirm the dialog.
5. You'll land on **History & Logs** with the new run selected — watch the Status column until it says **Completed**.

### Backfill a specific date range

1. Go to **Manual Run**.
2. Set Mode to **Invoice date**.
3. Set **Invoice Date From** and **Invoice Date To**.
4. Click **Manual Run**, confirm.
5. A "Find Gaps (Pass-2)" follow-up row will appear automatically underneath — let it finish too before considering the backfill complete.

### Re-check one or a few specific invoices you know the numbers of

1. Go to **Manual Run**.
2. Set Mode to **Invoice number list (comma-separated)**.
3. Type the reference numbers, comma-separated, e.g. `RSRE_000284, RSRE_000301`.
4. Click **Manual Run**, confirm.

### Test something unfamiliar before running it for real

1. Go to **Manual Run**, check **Dry run** — Max invoices to process automatically becomes 10, so the test stays quick.
2. Set up whatever you want to test (a new date range, a mode you don't usually use) and click **Manual Run**.
3. Check **History & Logs** — the row shows **"(Dry Run)"** in Mode, and every count is labeled as simulated, so it's never mistaken for a real result.
4. Uncheck **Dry run** (Max invoices resets to 0 automatically) and, if the test looked right, run it again for real.

### Force-reprocess an invoice that's already marked as imported

Use this only after confirming, in Sage 50 itself, that the invoice genuinely needs to go in again (e.g. it was accidentally deleted from Sage 50, or a mapping fix means it needs a fresh post).

1. Go to **Manual Run**.
2. Set Mode to **Invoice number list (comma-separated)** (or Invoice date / Invoice number range, whichever fits) and enter the invoice(s).
3. Check **Override "Already Imported" check for this run**.
4. Click **Manual Run** — you'll get an extra warning dialog specifically about this checkbox; confirm it only once you're sure the invoice isn't still sitting in Sage 50.
5. Confirm the normal run dialog too. The run's Mode will show **"(Override)"** in History & Logs afterward, so it's clearly marked.

### Compare PortPro customers against Sage 50 and push a handful of changes

1. Go to **Customer Refresh**. Confirm the **"Viewing data for:"** dropdown matches the current Target Sage50 path (it will, unless you'd previously selected a different one).
2. Click **Extract All Customer** and wait for it to finish — the grid fills with every PortPro customer, INSERT rows shown in red.
3. Use the search box to jump to specific customers if needed, or just scan the list.
4. Tick the rows you want to process (Select all, individual checkboxes, or spacebar).
5. Leave **Dry run** unchecked for a real write, or check it first to preview safely (the confirmation dialog will say so clearly either way).
6. Click **Run Selected**, confirm. The Applied/Date columns update in place for the rows you ran; the rest of the grid stays exactly as it was.

### Clean up an old test run from History & Logs

1. Go to **History & Logs**.
2. For one run: click its **Delete** button. For several: tick each row's **Select** box (Select all, or spacebar), then click **Delete Selected** (top-right, in red).
3. Confirm — this permanently removes that run's files, any failed-transaction report it made, and its tracking rows in the local database, so its invoices look brand new again if a real run ever touches them.

### Confirm a setting change actually took effect

Remember: a running Automatic Service keeps using its *old* settings until restarted.

1. Make your change on whichever tab, click that tab's **Save** button.
2. Go to **Automatic Sync**, click **Stop Automatic Service**.
3. Click **Start Automatic Service** again.
4. (Optional) Check the top bar's version label and the confirmation dialog shown when starting — it summarizes the settings actually in effect for this run.

### An invoice needs to be re-imported after fixing an account mapping

1. Fix the mapping on the **Sage 50** tab (Charge account map or Tax codes), click **Save Sage 50 settings**.
2. Go to **Manual Run**, Mode = **Invoice number list (comma-separated)**, enter that invoice's reference number.
3. Consider checking **Dry run** on the Sage 50 tab first to confirm the fix actually resolves it, before running for real.

### Something looks stuck — check what's actually running

1. Look at the **Process:** line in the top bar — it always reflects reality (Not running / Automatic / Manual, with a PID and start time).
2. If something genuinely needs stopping, use the top bar's **Stop** button — it works regardless of which tab started the process.

### Switching from a DEV/test Sage 50 file to the real PROD file

1. Go to **Sage 50** tab, update **Company data path** to the PROD `.SAI` file, click **Test Connection** to confirm it opens.
2. Click **Save Sage 50 settings**. The top bar's **Target Sage50** banner updates immediately.
3. Check **History & Logs** and **Customer Refresh** — their path dropdowns now offer PROD (fresh, or with its own prior history if you've used it before); DEV's own history is still there under its own dropdown entry, untouched.
4. Run normally from here — the skip logic automatically follows PROD's own tracking, not DEV's. See [section 15](#15-working-with-more-than-one-sage-50-company-file) for the full picture.

---

## 17. Troubleshooting / FAQ

**Q: I changed a setting and nothing seems different.**
A: Did you click that tab's Save button? And if the Automatic Service was already running, did you restart it afterward? See the walkthrough above.

**Q: The Manual Run / Start Automatic Service button is grayed out.**
A: The other process (Automatic Service, or a Manual Run) is currently active — check the **Process:** line in the top bar. Stop it first.

**Q: A run shows "Interrupted (no result)" or "Interrupted (partial)".**
A: The process stopped before finishing cleanly — crashed, was force-closed, or hit a fatal Sage 50 write error. Nothing already successfully imported is lost or will be double-imported; re-running the same range (or letting Pass-1 continue on its own) picks up exactly where it left off. Check **Full log** for what actually happened.

**Q: A "Find Gaps (Pass-2)" row found 0 invoices — is that a problem?**
A: No — see [section 14](#14-understanding-automatic-gap-fill-finding-the-gap). That's the normal, healthy result most of the time.

**Q: An invoice was imported twice.**
A: This shouldn't happen under normal operation — already-imported invoices are tracked and skipped automatically. If you suspect it did happen, check **Validate Invoice Extracted** for that invoice's Request ID(s) in History & Logs, and see the Settings tab's "Reset Imported-Invoice Tracking" section for how the tracking works (and why clearing it, or deliberately checking "Override Already Imported check," can cause exactly this if the Sage 50 company file already has the data).

**Q: I deleted a run from History & Logs by mistake — can I get it back?**
A: No — deleting a run is permanent, by design (it's meant to actually clean things up, not hide them). Its request/result files, any failed-transaction report, and its tracking rows are all genuinely removed. If the invoices it imported are still sitting correctly in Sage 50, nothing about Sage 50 itself is affected — only this app's own history and bookkeeping.

**Q: I switched Sage 50 paths and now History & Logs / Customer Refresh looks empty.**
A: That's expected the first time you point the app at a genuinely new company file — its tracking starts fresh. Use the path dropdown on either tab to switch back and confirm your old path's data is still there. See [section 15](#15-working-with-more-than-one-sage-50-company-file).

**Q: Sage 50 says an account "doesn't exist" but I can see it right there in the chart of accounts.**
A: This is a known Sage 50 SDK quirk — see **Accounts To Trust** on the Sage 50 tab.

**Q: Where do failed-transaction emails come from, and who gets them?**
A: Configured on the **Settings** tab, under Email. If Enabled is unchecked, no email goes out, but a CSV report is still saved to the Failed transactions folder for every run with a failure.

---

## 18. Glossary

- **Watermark** — the saved bookmark (a date, plus an invoice number kept for reference only) that Pass-1 and the Automatic Service use to know where they left off. One editable field on the Automatic Sync tab ([section 5](#5-automatic-sync-tab)). Scoped per Sage 50 path — see [section 15](#15-working-with-more-than-one-sage-50-company-file).
- **Continuous (Pass-1)** — the Automatic Service's main poll cycle, watermark-driven, based on each invoice's own creation date. See [section 5](#5-automatic-sync-tab).
- **Find Gaps (Pass-2) / "Finding the Gap"** — the automatic follow-up sweep that runs after almost every other run, double-checking for invoices the bulk list view might have silently missed. See [section 14](#14-understanding-automatic-gap-fill-finding-the-gap).
- **InvDate Changed (Pass-3)** — not implemented. A planned future pass to catch an invoice edited after its own creation date; needs a Reverse + Insert capability that doesn't exist yet. See [section 5](#5-automatic-sync-tab).
- **Override Already Imported check** — a Manual Run checkbox that lets a previously-imported invoice be re-processed for one run only, instead of being silently skipped. Never persisted; see [section 4](#4-manual-run-tab).
- **Dry run** — a mode that simulates a run without writing anything real to Sage 50. There are two independent Dry Run flags in the app: the shared one (Sage 50 tab / Manual Run tab) and Customer Refresh's own — see [section 6](#6-customer-refresh-tab).
- **Trigger folder** — the folder the running Service watches for new manual/trigger requests.
- **Request ID** — the unique internal ID assigned to a single run; shown in full in History & Logs, with a shorter "#" number for easier reference in conversation.
- **Cutoff (Lower) Invoice Date** — the hard floor date below which no invoice is ever processed, to preempt Sage 50 rejecting old-dated transactions.
- **Processing Delay (Days)** — how many of the most recent days are held back from Pass-1/watermark-driven processing, on a rolling basis.
- **Sage 50 path scoping** — the app tracks already-imported invoices, the watermark, and Customer Refresh results separately for each distinct Sage 50 company-file path, so switching between company files (e.g. DEV vs PROD) never mixes up their tracking. See [section 15](#15-working-with-more-than-one-sage-50-company-file).
