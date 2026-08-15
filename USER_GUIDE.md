# PortProSage Admin — User Guide

This is the complete, walk-through reference for the PortProSage Admin
application — the tool RS Rush Transfer Xpress Inc. uses to sync PortPro
invoices into Sage 50 (Canadian Edition). If the blue **"?"** help icon next
to a field doesn't answer your question, this guide should.

Open this guide any time from inside the app: click the **Help** button in
the top bar.

> Looking for the technical/developer documentation instead (architecture,
> file formats, how the Windows Service itself works)? See `README.md` in
> the same folder as this file.

---

## Table of Contents

1. [What this app actually does](#1-what-this-app-actually-does)
2. [Before you start: two processes, one Sage 50 seat](#2-before-you-start-two-processes-one-sage-50-seat)
3. [The top bar](#3-the-top-bar)
4. [Manual Run tab](#4-manual-run-tab)
5. [Automatic Sync tab](#5-automatic-sync-tab)
6. [Watermark tab](#6-watermark-tab)
7. [History & Logs tab](#7-history--logs-tab)
8. [PortPro tab](#8-portpro-tab)
9. [Sage 50 tab](#9-sage-50-tab)
10. [Settings tab](#10-settings-tab)
11. [About tab](#11-about-tab)
12. [Understanding automatic gap-fill ("Finding the Gap")](#12-understanding-automatic-gap-fill-finding-the-gap)
13. [Walkthroughs: common tasks step by step](#13-walkthroughs-common-tasks-step-by-step)
14. [Troubleshooting / FAQ](#14-troubleshooting--faq)
15. [Glossary](#15-glossary)

---

## 1. What this app actually does

The Admin app itself **never talks to PortPro or Sage 50 directly**. All it
does is:

1. Read and edit two settings files (`appsettings.json` and
   `appsettings.Local.json`) that live in the **Service folder**.
2. When you click a Run/Start button, either launch
   `PortProSage.Service.exe` (the program that actually does the work) or
   drop a small request file for an already-running one to pick up.

So editing a field and NOT clicking that tab's **Save** button does nothing
— and clicking Save does not, by itself, run anything either. The two
actions are separate on purpose: you can change settings any time without
accidentally kicking off a sync.

Every field in the app has a small circular blue **"?"** icon next to it —
click it for a plain-language explanation and a worked example, right where
you're looking. This guide covers the same ground in more depth, plus how
the tabs work together.

At the very bottom of the window is a status bar that shows something like
`Source: appsettings.json → Sync:PollingIntervalMinutes` whenever you click
into a field — so you always know exactly which file, and which setting
inside it, you're editing.

---

## 2. Before you start: two processes, one Sage 50 seat

There are two distinct ways to run a sync, and they are **mutually
exclusive** — you can only ever have one active at a time:

| | Manual Run | Automatic Sync |
|---|---|---|
| You start it from | **Manual Run** tab | **Automatic Sync** tab |
| What actually launches | A one-shot process that runs once and exits | A long-running process that keeps polling |
| You choose | Exactly which invoices to process (by date, range, list, or "continue") | Nothing per-run — it always continues from the watermark |
| Runs until | It finishes (usually seconds to a few minutes) | You click Stop, or the machine restarts |

**Why they can't run together:** both would try to open the same Sage 50
company file under the same Sage 50 username at the same time. Sage 50
rejects a second simultaneous session under one username — so the app
disables whichever button would create a conflict, and the top bar's
**Process:** status line always tells you which one (if either) is
currently active.

If you need to run something manually while the Automatic Service is on,
stop the Automatic Service first (top bar **Stop** button, or the Stop
button on the Automatic Sync tab), do your manual run, then start it again.

---

## 3. The top bar

Visible above every tab, at all times:

- **Service folder** — the folder containing `appsettings.json`,
  `appsettings.Local.json`, and `PortProSage.Service.exe`. The app guesses
  this on first launch and remembers whatever you last pointed it at.
  - **Browse...** — pick a different folder (a full folder picker dialog).
  - **Reload** — re-read settings from whatever folder is currently typed
    in the box, without opening the picker. Use this if you edited the
    settings files by hand outside the app.
- **Help** — opens this User Guide in whatever program handles `.md` files
  on your computer (Notepad, VS Code, a browser, etc).
- **v2.01.21** (top-right, gray) — the exact build number of the Admin app
  you're currently running. Useful when confirming "did the new build
  actually install" — compare this against what you were told to expect.
- **Process:** — always shows the real, current state:
  - **Not running** (red) — nothing is active; either button is free to use.
  - **Automatic Service running - PID 1234, since 9:03 AM** (green).
  - **Manual Run running - PID 5678, since 9:15 AM** (orange).
  - A small spinning progress bar appears next to this label whenever
    anything is active.
- **Stop** (top bar) — stops whichever process is currently running,
  without needing to switch to the tab that started it. Confirms first.

---

## 4. Manual Run tab

Use this to run a sync **exactly once, right now**, with full control over
exactly which invoices get processed.

### Mode dropdown — the five ways to pick invoices

| Mode | Selects invoices by | When to use it |
|---|---|---|
| **Invoice date** (default) | The invoice's own PortPro billing date, within your From/To window | The safe default for "process everything invoiced in this window." Can't accidentally pull in something merely *edited* recently that's actually dated long ago. |
| **Continue (from where we left off)** | The saved watermark — automatically resumes right after the last invoice this app successfully processed | Routine day-to-day catch-up runs. No dates or numbers to fill in at all. |
| **Last changed date** | PortPro's "last updated" timestamp, within your From/To window | Chasing "what changed recently" specifically. **Caution:** this can pull in an invoice dated well outside your window if it was merely edited — which has caused real failures before (an old invoice gets rejected by Sage 50's own "don't allow transactions before this date" rule, killing the whole run). Prefer Invoice date unless you specifically need this. |
| **Invoice number range** | Reference numbers between a Start and an End (both ends included) | You know the numeric range of what's missing, e.g. "everything between RSRE_000090 and RSRE_000095." |
| **Invoice number list (comma-separated)** | An explicit, exact list of reference numbers you type in | You know precisely which invoice(s) you need, e.g. re-checking one specific invoice that failed earlier. This mode looks each one up individually rather than paging through PortPro's list — which is also why it's more reliable at finding an invoice the list view sometimes misses (see [section 12](#12-understanding-automatic-gap-fill-finding-the-gap)). |

Every mode except Continue is a **one-time override** for this run only —
none of them read or change the saved "continue from" position.

#### Example — Invoice date

> **Goal:** Import everything invoiced in July 2026.
> Set Mode = **Invoice date**, From = `2026-07-01`, To = `2026-07-31`, then
> click **Manual Run**.

#### Example — Invoice number list

> **Goal:** Re-check three specific invoices that came back as failures
> last week.
> Set Mode = **Invoice number list (comma-separated)**, and type:
> `RSRE_000284, RSRE_000301, RSRE_000455`
> then click **Manual Run**.

### Other fields on this tab

- **Cutoff (Lower) Invoice Date** — a hard floor: no invoice dated before
  this date is *ever* processed, by Manual Run or the Automatic Service, no
  matter which Mode is used. This exists specifically to stop Sage 50's own
  "Do Not Allow Transactions Dated Before…" rule from rejecting an invoice
  mid-run and killing everything after it. This field is **shared** with
  the Automatic Sync tab (it's the exact same setting shown twice) and
  saves the instant you change it — you don't need to click a Save button
  for this one field.
- **Start invoice number / End invoice number** — only used by Invoice
  number range mode. Leave either blank for "no bound in that direction."
- **Invoice number list (comma-separated)** — only used by Invoice number
  list mode.
- **Max invoices to process (0 = no limit)** — caps how many *eligible*
  invoices (amount greater than zero) this run will actually process, on
  top of whatever Mode already selected. Example: Mode = Continue, Max = 10
  processes only the next 10 unprocessed invoices even if 50 have changed.
  This resets to 0 after every run — it's meant as a one-time safety cap,
  not a permanent limit.
- **Show command window while running** — checked (the default) pops up
  the Service's console window so you can watch it work live; unchecked
  runs it hidden in the background (you'd check progress via History &
  Logs instead). Also **shared** with the Automatic Sync tab, and also
  saves instantly.
- **Current write mode:** — a read-only reminder showing either
  `DRY RUN (simulated - nothing written to Sage 50)` or
  `REAL WRITE (changes Sage 50 for real)`. This mirrors the **Dry run**
  checkbox on the Sage 50 tab — check that tab if this says something
  other than what you expected.

### Previous Run (read-only)

A snapshot of the most recently completed run — whether it was started
from here, from Automatic Sync, or from a trigger file — so you can always
see at a glance what actually happened last, without digging into History
& Logs. Shows Mode, date/invoice range, Max invoices, the first and last
invoice actually processed, a SUCCESS / FINISHED WITH ERRORS / INTERRUPTED
result line, and (for an Invoice number list or gap-fill run) the exact
invoice list that was used.

### Buttons

- **Manual Run** — validates your inputs first (catches things like an End
  date before the From date, or an empty invoice list), warns you if Sage
  50 already appears to be open under a possibly-conflicting session, shows
  you a confirmation dialog summarizing exactly what's about to run
  (write mode, company file, mode, range, cap), and only then actually
  starts it. Automatically switches you to the History & Logs tab and
  highlights the new run.
- **Stop Manual Run** — only enabled while a manual run is active. Sends a
  graceful shutdown signal first (so anything already imported, and the
  watermark, stay correctly recorded up to that point) and only force-kills
  the process if it doesn't respond within 5 seconds.
- **Save** — remembers your current field values (Mode, dates, invoice
  numbers, Max) so they're already filled in next time you open the app.
  This also happens automatically the moment you click Manual Run, so you
  rarely need to click Save by itself.

---

## 5. Automatic Sync tab

Use this to configure and control the background service that runs
continuously, watching for new/changed invoices on its own schedule.

### Fields

- **Automatic Sync - Processing Delay (Days)** — holds back the most
  recent N days before they're eligible to sync. A live readout next to the
  field ("Upper cutoff date: today − 7 day(s)") shows exactly what date
  that currently resolves to. This is a rolling delay, not a permanent
  skip — a held-back invoice simply becomes eligible once it's old enough.
  Set to 0 to disable the delay entirely.
- **Automatic Sync - Polling Interval (minutes)** — how often the service
  checks PortPro for changed invoices on its own. (Manual requests dropped
  into the trigger folder are always picked up within about 15 seconds,
  regardless of this setting.)
- **Cutoff (Lower) Invoice Date** and **Show command window while
  running** — the same two shared fields described under Manual Run above;
  changing either here changes it everywhere, and both save instantly.

### Previous Run

Identical in content and layout to the Manual Run tab's Previous Run
section — it's genuinely the same underlying data, just also shown here so
you don't need to switch tabs to check it.

### Buttons

- **Save Automatic Sync settings** — saves the Polling Interval and
  Processing Delay to `appsettings.json`.
- **Start Automatic Service** — same pre-flight checks as Manual Run
  (nothing else running, Sage 50 not already open elsewhere), shows a
  confirmation summarizing write mode, company file, Sage 50 username,
  PortPro base URL, polling interval, and the auto-create customer/item
  settings, then starts the long-running background process.
- **Stop Automatic Service** — confirms, then gracefully stops it (falling
  back to a forced stop only if it doesn't respond).

### Important: settings changes need a restart

If the Automatic Service is already running and you change/Save a setting
anywhere in the app (polling interval, cutoff date, account mappings,
anything), **the running process keeps using the old values** until you
stop and restart it. The app reminds you of this after every Save.

---

## 6. Watermark tab

The "watermark" is the saved bookmark — a date plus an invoice number —
that Continue mode and the Automatic Service's own polling both use to know
where they left off. This tab lets you view it, and, if truly necessary,
override it by hand.

### Current Watermark (read-only)

Shows the watermark date and invoice number currently saved, with a
**Refresh** button. Reads `(none - no run has ever completed)` if nothing
has ever synced successfully yet.

### Reset / Change Watermark

- **New Watermark Date** — check the box and pick a date to set the
  watermark explicitly; leave it **unchecked** to clear the watermark
  entirely (the next Continue run then behaves as if nothing had ever
  synced, starting from the Processing Delay's upper bound instead).
- **New Watermark Invoice #** — for your own reference/record-keeping only
  — it does not itself control what gets fetched (the date does).

Both fields start out pre-filled with a copy of the current watermark, so
you're editing a real starting point rather than a blank form.

> **This can move the watermark backward, not just forward.** Doing so
> makes the next Continue run re-check invoices in the newly-covered range
> — but it will not create duplicates: every invoice already recorded as
> imported (tracked separately, by its own PortPro ID) is still recognized
> and skipped. Only genuinely missed ones get imported.

### Save button

Blocked entirely while any run is active (to avoid two things writing to
the same tracking database at once). Shows a clear WARNING dialog comparing
your old and new values before committing — the old value is not
recoverable once overwritten, so double-check before confirming.

---

## 7. History & Logs tab

Your record of every run that's ever happened — automatic, manual, or from
a trigger file — with full drill-down detail.

### The grid

Columns, left to right:

| Column | Meaning |
|---|---|
| **#** | A short, stable reference number for the run (assigned in the order it happened; never renumbers as new runs are added — easier to say/type than the full Request ID). |
| **Request ID** | The full internal ID for this run. |
| **Source** | "Automatic Service", "Manual Run", or "Trigger file". |
| **Mode** | Which selection mode was used (see [section 4](#4-manual-run-tab)); a gap-fill run shows as **"Finding the Gap (found/checked)"** once finished — see [section 12](#12-understanding-automatic-gap-fill-finding-the-gap). |
| **Process Start / Process End** | When the run's process actually started and finished. |
| **Inv Start Date / Inv End Date** | The actual invoice-date window this run covered. |
| **Fetched** | How many invoices PortPro returned for this run (shown as `found/checked` for a gap-fill run — see section 12). |
| **Imported** | How many were actually written to Sage 50. |
| **Skipped** | How many were skipped because they were already imported previously. |
| **Not found** | How many candidates this run specifically looked up one at a time and got a "doesn't exist" answer for. Only ever non-zero for an Invoice number list run or a gap-fill run — the other modes don't check individual candidates this way. |
| **Zero/-ve Amt** | Skipped because the invoice total was zero or negative (nothing to post). |
| **Before cutoff** | Skipped because the invoice was dated before your Cutoff (Lower) Invoice Date. |
| **Failed validation** | Failed a pre-import check (e.g. unmatched account) before ever reaching Sage 50. |
| **Failed write** | Passed validation but the actual write to Sage 50 failed. |
| **Status** | Completed / Running / Interrupted (partial) / Interrupted (no result) / Skipped / Pending (queued). |

Click **Refresh** to reload from disk (a "Last refreshed: HH:mm:ss" label
next to it confirms when). The grid does *not* auto-refresh every couple of
seconds anymore while something is running — only when you click Refresh,
or once a run genuinely finishes — specifically to stop the list from
flickering while you're trying to read it.

### The detail tabs (for whichever row you've selected)

- **Summary** — a full plain-text readout of everything about the run:
  request details, the exact range/list used, every count (including the
  same "found/checked" framing as the grid), duration, and the watermark
  before/after.
- **Per-invoice outcomes** — one row per invoice this run touched: invoice
  #, PortPro date, success/fail, the resulting Sage 50 invoice number, and
  any messages (e.g. why it failed).
- **Invoice Transferred** — one row per invoice that was actually written
  to Sage 50: PortPro #/date, Sage 50 #/date, total amount, tax charged.
- **Warnings / Validation** — just the warning/validation lines from this
  run's log, filtered out of the noise.
- **Failed Transactions** — just the error/failure lines.
- **Full log** — the complete raw log text for this run's time window,
  with a search box that filters as you type.

### Reading a gap-fill row

A "Finding the Gap" row is automatically created after almost every other
run (see next section) — it's the app double-checking the range it just
covered. If you see one with, say, "Fetched: 0/79" and "Not found: 79",
that means it checked 79 candidate invoice numbers individually and
genuinely didn't find any of them — that's a normal, healthy result (gaps
in invoice numbering are completely ordinary), not an error.

---

## 8. PortPro tab

Connection settings for PortPro's API. You'll rarely need to touch most of
these after initial setup:

- **Base URL** / **Invoice endpoint** — where PortPro's API lives and where
  invoice data comes from. Only changes if PortPro moves their API.
- **Access token endpoint** — kept for reference; the real login flow below
  uses **New token endpoint** instead.
- **New token endpoint** — where a fresh access token is requested using
  the Refresh token, automatically, whenever the current one expires. You
  never need to trigger this by hand.
- **Page size** — how many invoices PortPro returns per page (the app
  transparently pages through everything, this just controls the page
  size). 
- **Timeout (seconds)** — how long to wait for PortPro to respond before
  giving up on that request.
- **Access token** / **Refresh token** (secrets) — your PortPro API
  credentials. The access token refreshes itself automatically using the
  refresh token; you'd only ever hand-enter these once, during initial
  setup, using the real values from PortPro's own integration settings
  screen. Use **Test Connection** to confirm the currently **saved**
  credentials actually work (not unsaved edits — Save first, then Test).

Click **Save PortPro settings** to persist changes.

---

## 9. Sage 50 tab

Connection credentials and the account-mapping rules that decide exactly
where each invoice line posts in Sage 50. This is the most consequential
tab in the app — take the Dry run switch seriously.

- **App name** / **App ID** — how this app identifies itself to the Sage 50
  SDK. Set once during setup, rarely changed.
- **Company data path** — the full path to your `.SAI` company file. Use
  **Test Connection** to confirm the app can actually open it.
- **Sage50 User Name** / **Password** — must be a **dedicated account**,
  never one a person also logs into interactively, since Sage 50 rejects a
  second simultaneous session under the same username.
- **Expected SDK version** — optional; if set, logs a warning if the
  installed SDK's version doesn't match. Leave blank to skip the check.
- **Default revenue account** — the GL account used for a charge that has
  no specific mapping below (see Charge account map). If this is also
  blank, an unmapped charge causes that invoice to fail outright rather
  than posting somewhere undefined.
- **Ignore account 1-on-1 match and apply default** — normally, if a
  mapped account can't be confirmed as real in Sage 50, the run stops on
  that invoice. Checking this makes it instead just fall back to the
  Default revenue account and log a warning. Either way, if the *default*
  account itself can't be confirmed, the run always stops — there's
  nothing left to fall back to.
- **Default receivable account** — the GL A/R account used for any
  customer this app auto-creates.
- **Accounts To Trust (comma-separated)** — a workaround for a known Sage
  50 SDK quirk where a real, existing account is sometimes wrongly
  reported as "does not exist." If a run fails with that specific error for
  an account you've manually confirmed *is* real in Sage 50, add its number
  here (comma-separated with any others). **Do not** add an account here
  that's genuinely missing — fix the actual setup instead; this field is
  only for confirmed-real-but-misreported accounts.
- **Auto-create missing customers** — checked: an unrecognized PortPro
  customer is created automatically before posting. Unchecked: that
  invoice fails validation instead ("customer not found").
- **Auto-create missing items/services** — the same idea, for charge/item
  lines.
- **Dry run (simulate writes - no real Sage 50 changes)** — **the most
  important switch on this screen.** Checked: nothing is actually written
  to Sage 50 — the run logs exactly what it *would* do instead. Always test
  a change (a new date range, a new account mapping, anything unfamiliar)
  with this checked first, confirm the log looks right, then uncheck it for
  the real run.
- **Tax codes** grid — maps a Canadian tax abbreviation found in a PortPro
  charge name (HST/GST/PST/QST) to the matching Sage 50 tax code (from
  Sage 50's own Setup ▸ Settings ▸ Company ▸ Sales Taxes ▸ Tax Codes
  screen). A recognized tax charge is **not** posted as its own line —
  Sage 50 applies the tax code directly to the revenue lines instead.
- **Charge account map** grid — maps each PortPro charge name (e.g. "PICK
  UP & DELIVERY", "FUEL SURCHARGE 1") to the Sage 50 GL account it should
  post to. Matched case-insensitively against each invoice line. Only the
  **Sage 50 Account Number** column actually affects posting — the glCode
  and account name columns are reference/audit only. A charge with a blank
  account number here falls back to Default revenue account.

Click **Save Sage 50 settings** to persist changes.

---

## 10. Settings tab

Everything else: email notifications, where files live, log cleanup, and a
destructive reset utility.

### Email (failed-transaction reports)

- **Enabled** — master switch. When unchecked, a failed-transaction CSV is
  still saved to disk on every run with a failure, but no email is sent,
  and every field below is ignored.
- **SMTP host / port / Use SSL** — your outgoing mail server settings.
- **From address / Username / Password** — the sending account. Some
  providers (Gmail, Microsoft 365) require an app-specific password here,
  not your normal login password.
- **Recipients (comma-separated)** — everyone who should get a report
  whenever a run has at least one failure, e.g.
  `ashwani@smallarc.com, accounting@rushtransfer.com`.

### Folder Locations

Each has an **Open** button to jump straight to it in File Explorer:

- **Trigger folder** — where new manual/trigger requests are dropped for
  the Service to pick up.
- **Processed trigger folder** — where a request moves once handled; this
  is what History & Logs actually reads from.
- **State database path** — the file tracking already-imported invoices
  and the watermark. **Never point two different client installs at the
  same file.**
- **Log folder** — daily rolling log files.
- **Failed transactions folder** — CSV reports, one per run that had a
  failure, regardless of whether email is enabled.
- **Minimum log level** — Information is the normal, recommended setting;
  switch to Debug only while actively troubleshooting something (it's much
  noisier).
- **Cleanup log after execution (Days)** — old log files past this many
  days are deleted automatically at the end of every run. This is
  **permanent** — set to 0 to disable cleanup entirely. **Apply Now**
  triggers the cleanup immediately using whatever value is currently
  saved (Save first if you just changed the number).

### Reset Imported-Invoice Tracking

- **Currently tracked as already imported** — a live count of how many
  invoices this app believes it has already posted (this is how it decides
  to silently skip something as "already imported" without re-checking
  Sage 50 itself every time). If you've switched Sage 50 company files,
  this count still reflects the *old* file until reset.
- **Clear All Imported-Invoice Records** — ⚠️ **irreversible.** Wipes this
  app's entire memory of what's already been imported (does not touch
  Sage 50 itself in any way). After clearing, every invoice looks brand
  new on the next run. This is only safe to do if you're certain the
  current Sage 50 company file genuinely doesn't already contain those
  invoices — otherwise they will be **posted again as duplicates**. Blocked
  entirely while any run is active.

Click **Save Settings** to persist the non-secret fields above (Email
password saves too, just to the separate secrets file).

---

## 11. About tab

Static reference info: app version, SmallArc Inc. contact details (phone
numbers for US/Canada, `contact@smallarc.com`, with a one-click Copy
button), and a note about SmallArc's other product, Fixyee
(`www.fixyee.com`). No functional controls here — just company/licensing
information.

---

## 12. Understanding automatic gap-fill ("Finding the Gap")

This is the one behavior in the app that isn't a button you click — it
just happens, automatically, after almost every run. It's worth
understanding so a "Finding the Gap" row in History & Logs doesn't look
like a mystery.

### Why it exists

PortPro's normal invoice list — the one every date-range or number-range
run uses — can, under certain conditions, silently leave out real invoices
that genuinely exist. Looking that invoice up *individually*, by its exact
reference number, finds it fine; it's specifically the bulk list view that
can miss it. Since there's no reliable way to know in advance whether a
given run hit this, the app doesn't rely on you to remember to check —
it checks automatically, every time.

### How it works

After any range-based run finishes (Manual Run or the Automatic Service,
any Mode), the app automatically looks at the exact invoice-number range
that run actually touched (from the lowest to the highest invoice number it
saw), figures out which numbers in that range are *not* already recorded as
imported, and looks each one up individually — the same reliable
one-at-a-time lookup used by Invoice number list mode.

This shows up in History & Logs as its **own separate row**, one level
below the run that triggered it, labeled **"Finding the Gap"**. You never
select this yourself — there's no dropdown option for it.

### Reading the result

Once a gap-fill row finishes, its Mode column shows something like:

```
Finding the Gap (19/98)
```

That means: 98 candidate invoice numbers in the range were checked one by
one, and 19 of them turned out to be real invoices that genuinely existed
(and have now been imported). The other 79 were checked and confirmed to
simply not exist — which is completely normal; invoice numbering in any
system has gaps (voided invoices, numbers reserved and never used, etc.).

The same 19/98-style breakdown appears in the grid's **Fetched** and **Not
found** columns for that row, and in full detail on the **Summary** tab if
you select that row.

**A gap-fill row with "0 found" is not a failure** — it means the sweep
found no genuinely missing invoices in that range, which is the expected,
healthy outcome most of the time.

---

## 13. Walkthroughs: common tasks step by step

### Run a normal catch-up sync manually, right now

1. Go to **Manual Run**.
2. Set Mode to **Continue (from where we left off)**.
3. Click **Manual Run**, confirm the dialog.
4. You'll land on **History & Logs** with the new run selected — watch the
   Status column until it says **Completed**.

### Backfill a specific date range

1. Go to **Manual Run**.
2. Set Mode to **Invoice date**.
3. Set **Invoice Date From** and **Invoice Date To**.
4. Click **Manual Run**, confirm.
5. A "Finding the Gap" follow-up row will appear automatically underneath —
   let it finish too before considering the backfill complete.

### Re-check one or a few specific invoices you know the numbers of

1. Go to **Manual Run**.
2. Set Mode to **Invoice number list (comma-separated)**.
3. Type the reference numbers, comma-separated, e.g.
   `RSRE_000284, RSRE_000301`.
4. Click **Manual Run**, confirm.

### Figure out why a run had failures

1. Go to **History & Logs**.
2. Select the run in the grid (look for **Failed validation** or
   **Failed write** greater than zero, or a Status other than Completed).
3. Check the **Summary** tab first for the overall counts.
4. Check **Failed Transactions** for the specific error message(s).
5. If you need the raw context around the failure, use **Full log** and
   its search box.

### Confirm a setting change actually took effect

Remember: a running Automatic Service keeps using its *old* settings until
restarted.

1. Make your change on whichever tab, click that tab's **Save** button.
2. Go to **Automatic Sync**, click **Stop Automatic Service**.
3. Click **Start Automatic Service** again.
4. (Optional) Check the top bar's version label and the confirmation
   dialog shown when starting — it summarizes the settings actually in
   effect for this run.

### An invoice needs to be re-imported after fixing an account mapping

1. Fix the mapping on the **Sage 50** tab (Charge account map or Tax
   codes), click **Save Sage 50 settings**.
2. Go to **Manual Run**, Mode = **Invoice number list (comma-separated)**,
   enter that invoice's reference number.
3. Consider checking **Dry run** on the Sage 50 tab first to confirm the
   fix actually resolves it, before running for real.

### Something looks stuck — check what's actually running

1. Look at the **Process:** line in the top bar — it always reflects
   reality (Not running / Automatic / Manual, with a PID and start time).
2. If something genuinely needs stopping, use the top bar's **Stop**
   button — it works regardless of which tab started the process.

---

## 14. Troubleshooting / FAQ

**Q: I changed a setting and nothing seems different.**
A: Did you click that tab's Save button? And if the Automatic Service was
already running, did you restart it afterward? See the walkthrough above.

**Q: The Manual Run / Start Automatic Service button is grayed out.**
A: The other process (Automatic Service, or a Manual Run) is currently
active — check the **Process:** line in the top bar. Stop it first.

**Q: A run shows "Interrupted (no result)" or "Interrupted (partial)".**
A: The process stopped before finishing cleanly — crashed, was force-
closed, or hit a fatal Sage 50 write error. Nothing already successfully
imported is lost or will be double-imported; re-running the same
range/Continue will pick up exactly where it left off. Check **Full log**
for what actually happened.

**Q: A "Finding the Gap" row found 0 invoices — is that a problem?**
A: No — see [section 12](#12-understanding-automatic-gap-fill-finding-the-gap).
That's the normal, healthy result most of the time.

**Q: An invoice was imported twice.**
A: This shouldn't happen under normal operation — already-imported
invoices are tracked and skipped automatically. If you suspect it did
happen, check **Per-invoice outcomes** for that invoice's Request ID(s) in
History & Logs, and see the Settings tab's "Reset Imported-Invoice
Tracking" section for how the tracking works (and why clearing it can
cause exactly this if the Sage 50 company file already has the data).

**Q: Sage 50 says an account "doesn't exist" but I can see it right there
in the chart of accounts.**
A: This is a known Sage 50 SDK quirk — see **Accounts To Trust** on the
Sage 50 tab.

**Q: Where do failed-transaction emails come from, and who gets them?**
A: Configured on the **Settings** tab, under Email. If Enabled is
unchecked, no email goes out, but a CSV report is still saved to the
Failed transactions folder for every run with a failure.

---

## 15. Glossary

- **Watermark** — the saved bookmark (a date + invoice number) that
  Continue mode and the Automatic Service use to know where they left off.
- **Continue mode** — the Mode option that resumes from the watermark
  automatically, with no dates/numbers to fill in.
- **Gap-fill / "Finding the Gap"** — the automatic follow-up sweep that
  runs after almost every other run, double-checking for invoices the bulk
  list view might have silently missed. See [section 12](#12-understanding-automatic-gap-fill-finding-the-gap).
- **Dry run** — a mode (Sage 50 tab checkbox) that simulates a run without
  writing anything real to Sage 50.
- **Trigger folder** — the folder the running Service watches for new
  manual/trigger requests.
- **Request ID** — the unique internal ID assigned to a single run; shown
  in full in History & Logs, with a shorter "#" number for easier
  reference in conversation.
- **Cutoff (Lower) Invoice Date** — the hard floor date below which no
  invoice is ever processed, to preempt Sage 50 rejecting old-dated
  transactions.
- **Processing Delay (Days)** — how many of the most recent days are held
  back from automatic/Continue processing, on a rolling basis.
