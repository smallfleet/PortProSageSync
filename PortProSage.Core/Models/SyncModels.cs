namespace PortProSage.Core.Models;

public enum FilterType
{
    /// <summary>Pull invoices whose PortPro "last changed" (updatedAt) falls in [From, To].</summary>
    LastChangedDate,

    /// <summary>Pull invoices whose reference/invoice number falls in [StartNumber, EndNumber] (inclusive).</summary>
    InvoiceNumberRange,

    /// <summary>Pull invoices whose load completed date falls in [From, To].</summary>
    CompletedDateRange,

    /// <summary>Pull a specific, explicit set of invoices by reference number
    /// (SyncRequest.InvoiceNumberList, comma-separated) - fetched one by one via
    /// PortPro's single-invoice endpoint (PortProClient.GetInvoiceAsync), NOT the
    /// paginated list endpoint InvoiceNumberRange uses. Added 2026-08-12 as a
    /// direct workaround for a confirmed gap: a real invoice (RSRE_000284) was
    /// invisible via the list endpoint under every query tried, but the single-
    /// invoice endpoint returned it fine. Use this for a small number of known,
    /// non-contiguous invoices - InvoiceNumberRange (which needs the list endpoint
    /// to find everything between two numbers) remains the right choice for a
    /// genuine range.</summary>
    InvoiceNumberList,

    /// <summary>Computes its own InvoiceNumberList candidate set from a Start/End
    /// range (same fields InvoiceNumberRange uses) minus whatever's already been
    /// successfully imported (SyncStateRepository.GetAllImportedReferenceNumbers),
    /// then rewrites itself into FilterType.InvoiceNumberList and proceeds through
    /// that exact same code path - see SyncOrchestrator.RunAsync's handling of this
    /// value and ReferenceNumberFormat for the prefix/number/suffix split. Added
    /// 2026-08-14 to systematically find every invoice affected by the multi-
    /// charge-set list-endpoint gap (see InvoiceNumberList's doc comment) across a
    /// whole range, instead of checking suspected gaps one at a time by hand.</summary>
    InvoiceNumberGapScan,

    /// <summary>Not an invoice sync at all - the EXECUTE half of the Admin app's
    /// "Customer Refresh" tab (MainForm.CustomerRefreshTab.cs; the enum name keeps
    /// its original "Full" wording internally even though the UI dropped it 2026-
    /// 08-23 in favor of an explicit per-row picker - see CustomerRefreshScan
    /// below for the PREVIEW half). Processes EXACTLY the PortPro customer ids in
    /// SyncRequest.CustomerRefreshSelectedPortProIds - never "all customers"
    /// implicitly - creating each one in Sage 50 if it doesn't already exist
    /// (INSERT) or pushing its changed profile if it does (UPDATE), bypassing the
    /// incremental "only if updatedAt changed" check CustomerSyncService.
    /// SyncChangedCustomersAsync normally applies. Handled entirely separately
    /// from SyncOrchestrator.RunAsync (see Diagnostics.RunOnceAsync) - no invoices
    /// are fetched or touched at all, so every Invoices* field on the resulting
    /// SyncResult is repurposed to carry customer counts instead (InvoicesFetched=
    /// customers selected, InvoicesImported=created+updated, InvoicesSkippedBeforeCutoff=
    /// created, InvoicesSkippedAlreadyImported=updated, InvoicesFailedImport=
    /// failed) - see RunHistoryService/MainForm.HistoryTab.cs's Customer-Refresh-
    /// specific display handling for where these are unpacked back into
    /// customer-appropriate labels.</summary>
    FullCustomerRefresh,

    /// <summary>The PREVIEW half of the Admin app's "Customer Refresh" tab -
    /// entirely read-only, no Sage 50 writes at all. Fetches every PortPro
    /// customer, checks each against Sage 50 by name (and, for a match, reads
    /// back its current Sage 50 profile too), and returns one
    /// CustomerRefreshCandidate per customer on SyncResult.CustomerRefreshCandidates
    /// describing what WOULD happen (INSERT/UPDATE) with a side-by-side PortPro-
    /// vs-Sage50 comparison - populates the Admin app's grid so the operator can
    /// pick exactly which ones to actually run via FullCustomerRefresh above.
    /// Still needs a real (if read-only) Sage 50 connection, so it's subject to
    /// the same one-at-a-time concurrency rule as every other run.</summary>
    CustomerRefreshScan
}

/// <summary>
/// A manual sync request. The Trigger CLI writes one of these as JSON into the
/// configured TriggerFolder; the Windows Service picks it up, processes it, and
/// writes a matching *.result.json file next to the archived request.
/// </summary>
public class SyncRequest
{
    public string RequestId { get; set; } = Guid.NewGuid().ToString("N");
    public FilterType FilterType { get; set; }

    public DateTimeOffset? From { get; set; }
    public DateTimeOffset? To { get; set; }

    public string? StartInvoiceNumber { get; set; }
    public string? EndInvoiceNumber { get; set; }

    /// <summary>Comma-separated reference numbers - only used by FilterType.
    /// InvoiceNumberList. Raw, unparsed/untrimmed text as entered; PortProClient.
    /// GetInvoicesAsync splits and trims it.</summary>
    public string? InvoiceNumberList { get; set; }

    /// <summary>
    /// True for the "continue from where we left off" default (no explicit range
    /// given) - used by both the automatic poll and a manual trigger run with no
    /// --mode specified. SyncOrchestrator resolves From/To from the persisted
    /// watermark when this is true, and only advances the watermark/last-processed-
    /// invoice-number tracking for requests with this set - an explicit range
    /// (UseWatermark false) is a one-time override that never touches persisted
    /// state, exactly as before/after this run.
    /// </summary>
    public bool UseWatermark { get; set; }

    /// <summary>Distinct from UseWatermark - this does NOT change what range gets
    /// fetched (an explicit From/To is used exactly as given), it only means the
    /// watermark should be advanced from what this run actually touched, same as
    /// a watermark-driven run does. Added 2026-08-25 for Manual Run's "Invoice
    /// date" mode: an explicit completed-date range shares no field with the
    /// watermark (which tracks PortPro's last-changed timestamp, a different
    /// date entirely - see FilterType.LastChangedDate's doc comment), so this is
    /// opt-in and defaults off; when on, SyncOrchestrator advances the watermark
    /// per invoice from that invoice's own real UpdatedAt, exactly like the
    /// UseWatermark path does, just without also overriding From/To from it.</summary>
    public bool AdvanceWatermarkOnCompletion { get; set; }

    /// <summary>
    /// Caps how many eligible (amount > 0) invoices this run will actually process,
    /// regardless of how many fall within the fetched range - confirmed live
    /// 2026-08-05 that computing a Start/End boundary from a separate "find N
    /// candidates" snapshot and handing it to a later, independently-refetched run
    /// doesn't reliably cap anything: PortPro is live data, so the two fetches can
    /// disagree, and the second run just processes everything in the boundary
    /// regardless of the original N. Enforcing the cap inside the same run's own
    /// loop is the only way it's actually a cap. Null means unlimited (the default
    /// for automatic polling and ordinary manual triggers). Not used at all by
    /// FilterType.FullCustomerRefresh as of 2026-08-23 - see
    /// CustomerRefreshSelectedPortProIds below, which replaced the old "max
    /// customers" cap with an explicit per-customer picker instead.
    /// </summary>
    public int? MaxInvoicesToProcess { get; set; }

    /// <summary>Only meaningful for FilterType.FullCustomerRefresh - see that enum
    /// value's doc comment. Deliberately NOT the same flag as Sage50Settings.
    /// DryRun (the one every other write in this app shares) - the Admin app's
    /// Customer Refresh tab has its own independent Dry Run checkbox (defaults to
    /// checked/true every time, never persisted, never affects or is affected by
    /// the shared Sage50 tab / Manual Run Dry Run checkbox). Diagnostics.
    /// RunFullCustomerRefreshAsync applies this by overriding Sage50Settings.DryRun
    /// in-memory for THIS PROCESS ONLY (same pattern as Diagnostics.
    /// RealTransferAsync/CreateTestItemAsync forcing DryRun off) - never touches
    /// appsettings.Local.json.</summary>
    public bool CustomerRefreshDryRun { get; set; } = true;

    /// <summary>Only meaningful for FilterType.FullCustomerRefresh - the exact set
    /// of PortPro customer ids to process (create or update, as appropriate),
    /// built from whichever rows the operator checked in the Admin app's Customer
    /// Refresh grid after a CustomerRefreshScan preview. Never "all customers" -
    /// see FilterType.FullCustomerRefresh's doc comment for why this replaced the
    /// old blanket "refresh everything" behavior and its "max customers" cap.</summary>
    public List<string>? CustomerRefreshSelectedPortProIds { get; set; }

    /// <summary>Bypasses SyncStateRepository.IsAlreadyImported's skip check for
    /// this run only - an invoice this app already recorded as imported gets
    /// re-validated and re-posted to Sage 50 instead of being silently skipped.
    /// Added 2026-08-16 as a one-time, per-run override for Manual Run's Invoice
    /// date/Invoice number range/Invoice number list modes (Admin's checkbox is
    /// deliberately never persisted and always resets to unchecked - see
    /// MainForm.RunTab.cs) - NOT for Continue or Last changed date, which drive
    /// the watermark and are meant to process only genuinely new/changed
    /// invoices. Checking this does NOT clear the existing imported_invoice
    /// tracking row; a successful re-import just overwrites it (see
    /// SyncStateRepository.MarkImported's upsert), so this is safe to use
    /// repeatedly, but it WILL create a genuine duplicate transaction in
    /// Sage 50 if the invoice is truly still there - it does not undo or replace
    /// the original Sage 50 invoice, only PortProSageSync's own memory of it.
    /// </summary>
    public bool OverrideAlreadyImportedCheck { get; set; }

    public DateTimeOffset RequestedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string RequestedBy { get; set; } = Environment.UserName;
}

public class SyncResult
{
    public string RequestId { get; set; } = string.Empty;
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset FinishedAtUtc { get; set; }

    /// <summary>False for every progress checkpoint written while the run is still
    /// in progress (see SyncOrchestrator.RunAsync's onProgress callback), true only
    /// on the very last write, once the run has genuinely finished. Confirmed live
    /// 2026-08-09 this was a real gap: a run that was killed/crashed mid-way had
    /// never written a result.json at all, so every count showed blank in History &
    /// Logs even though real progress had been made. Callers now overwrite the same
    /// result file after every invoice, so if the process dies mid-run, whatever
    /// was last checkpointed (IsFinal=false) survives as the historical record
    /// instead of nothing at all - see RunHistoryEntry.IsPending, which now checks
    /// this flag rather than just whether a result file exists.</summary>
    public bool IsFinal { get; set; }

    /// <summary>The OS process ID that actually ran this - a Manual Run gets its
    /// own dedicated process, but the Automatic Service's periodic poll and
    /// trigger-file processing all share one long-running process, so several
    /// history entries can (and normally will) show the same PID. Set once, at
    /// the very start of SyncOrchestrator.RunAsync, so every run type gets it for
    /// free.</summary>
    public int ProcessId { get; set; }

    /// <summary>Captured once, at the very start of SyncOrchestrator.RunAsync, from
    /// Sage50Settings.DryRun - so History &amp; Logs can label a run as simulated
    /// (Mode column, Summary) instead of leaving "Imported: 19" looking like 19
    /// real Sage 50 writes happened when nothing was actually written. Confirmed
    /// live 2026-08-22 this was a real gap - a completed Dry Run gave no visible
    /// indication anywhere in its own history record that it had been simulated.</summary>
    public bool WasDryRun { get; set; }

    /// <summary>Captured once, at the very start of the run, from Sage50Settings.
    /// CompanyDataPath - which Sage 50 company file this run actually targeted
    /// (or, for FilterType.CustomerRefreshScan, read-only checked against).
    /// Confirmed live 2026-08-24 this matters: the same state.db is shared across
    /// however many different Sage 50 company files get configured over time
    /// (e.g. a DEV file during testing, a PROD file for real use), and every
    /// History &amp; Logs entry needs to say which one it actually belongs to, both
    /// for the Admin app's path picker (MainForm.HistoryTab.cs/
    /// MainForm.CustomerRefreshTab.cs) and so a run against one file is never
    /// mistaken for one against another.</summary>
    public string? Sage50Path { get; set; }

    /// <summary>The actual comma-separated reference-number list this run used, for
    /// FilterType.InvoiceNumberList or InvoiceNumberGapScan requests - a manually-
    /// typed list is echoed back as-is; a gap scan's computed candidate list is
    /// recorded here too (SyncOrchestrator.RunAsync rewrites gap scan into
    /// InvoiceNumberList internally, so both end up populating this the same way).
    /// Null for every other filter type. Shown in History & Logs' Summary and the
    /// Previous Run section so a gap scan's actual candidate list is never just a
    /// black box.</summary>
    public string? ResolvedInvoiceNumberList { get; set; }

    /// <summary>True when this cycle was never actually attempted - the Automatic
    /// Service's pre-flight check (Worker.RunAutomaticLastChangedSyncAsync) found
    /// another PortProSage.Service.exe process already running and skipped this
    /// cycle rather than risk two processes opening Sage 50 at the same time. When
    /// true, StartedAtUtc == FinishedAtUtc (nothing actually ran) and every other
    /// count on this result is meaningless/zero - see SkipReason for why.</summary>
    public bool Skipped { get; set; }

    public string? SkipReason { get; set; }

    public int InvoicesFetched { get; set; }
    public int InvoicesImported { get; set; }
    public int InvoicesSkippedAlreadyImported { get; set; }
    public int InvoicesSkippedZeroOrNegativeAmount { get; set; }

    /// <summary>Candidates PortPro's single-invoice endpoint came back 404 for -
    /// only ever non-zero for InvoiceNumberList/InvoiceNumberGapScan requests,
    /// which are the only modes that check an explicit list of reference numbers
    /// one at a time and so are the only ones that can know a specific candidate
    /// simply doesn't exist. See PortProFetchResult.NotFoundCount.</summary>
    public int InvoicesNotFound { get; set; }

    /// <summary>Invoices whose own date (BillingDate, falling back to
    /// CompletedDate) fell before SyncSettings.CutoffInvoiceDate - see that
    /// property's doc comment for why this exists.</summary>
    public int InvoicesSkippedBeforeCutoff { get; set; }

    public int InvoicesFailedValidation { get; set; }
    public int InvoicesFailedImport { get; set; }

    /// <summary>Customers genuinely auto-created this run because an invoice
    /// needed one that didn't already exist in Sage 50 (InvoiceValidationService.
    /// ValidateCustomerAsync) - counted once per distinct customer, not once per
    /// invoice, since a later invoice for the same newly-created customer resolves
    /// from the per-run cache instead. Only meaningful for an ordinary invoice-sync
    /// run - the Customer Refresh tab's own Created/Updated counts (§ Diagnostics.
    /// RunFullCustomerRefreshAsync) are unrelated and reported separately.</summary>
    public int CustomersCreated { get; set; }

    /// <summary>Customers updated by this run's own trailing incremental sync
    /// sweep (CustomerSyncService.SyncChangedCustomersAsync, which never creates -
    /// only updates an existing Sage 50 customer whose PortPro profile changed).
    /// Set by the caller (Diagnostics.RunOnceAsync) after that sweep runs, since
    /// SyncOrchestrator.RunAsync itself returns before that sweep is even called.</summary>
    public int CustomersUpdated { get; set; }

    public List<InvoiceProcessingOutcome> Outcomes { get; set; } = new();

    /// <summary>The actual invoice-date window this run covered - captured once,
    /// right after SyncOrchestrator.RunAsync resolves request.From/To (whether
    /// from an explicit Invoice date/Last changed date range, or resolved from
    /// the watermark for a Continue run), before day-batching (see BatchCount)
    /// mutates request.From/To per batch. Null for Invoice number range mode,
    /// which has no date concept at all. Unlike WatermarkBeforeRun/AfterRun
    /// (which only ever move for a watermark-driven run), this is always
    /// populated for any date-based mode - it's what History & Logs' "Inv Start
    /// Date"/"Inv End Date" columns and the Previous Run section actually show,
    /// since the watermark is misleading for an explicit-range run that never
    /// touches it.</summary>
    public DateTimeOffset? EffectiveFromUtc { get; set; }
    public DateTimeOffset? EffectiveToUtc { get; set; }

    /// <summary>Always 1 - batching by day was removed 2026-08-14. Kept as a
    /// field (rather than removed) since History & Logs still reads it. 0 only
    /// for a run that never even started (e.g. the
    /// pre-flight Skipped case, or the "nothing to process yet" early exit).</summary>
    public int BatchCount { get; set; }

    /// <summary>
    /// Pre/post snapshot of the persisted "continue from where we left off" state -
    /// read once at the very start of the run and once at the very end, always
    /// (regardless of UseWatermark), so the two can be compared directly. For an
    /// explicit-range run (UseWatermark=false), Before and After should always be
    /// identical - that's the persisted-state-is-untouched guarantee made visible
    /// and checkable, not just documented. For a watermark-driven run, the
    /// difference between Before and After is exactly how far this run advanced.
    /// </summary>
    public DateTimeOffset? WatermarkBeforeRun { get; set; }
    public DateTimeOffset? WatermarkAfterRun { get; set; }
    public string? LastProcessedInvoiceNumberBeforeRun { get; set; }

    /// <summary>
    /// The persisted last-processed-invoice-number as of the end of the run -
    /// always populated (re-read from state at the end), not just for
    /// watermark-driven runs; see WatermarkBeforeRun/WatermarkAfterRun's doc
    /// comment for why reading unconditionally at both ends matters. For
    /// display/audit only; see SyncRequest.UseWatermark's doc comment for why the
    /// date-based watermark, not this number, is what actually drives the query.
    /// </summary>
    public string? LastProcessedInvoiceNumberAfterRun { get; set; }

    /// <summary>Only populated for FilterType.CustomerRefreshScan - one entry per
    /// PortPro customer found, describing what a subsequent FullCustomerRefresh
    /// run WOULD do for it. Null for every other FilterType.</summary>
    public List<CustomerRefreshCandidate>? CustomerRefreshCandidates { get; set; }

    /// <summary>Only populated for FilterType.FullCustomerRefresh (the EXECUTE
    /// half) - one entry per customer it actually attempted, so the Admin app can
    /// update the existing Customer Refresh grid's rows in place (Applied/Date
    /// columns) rather than clearing the grid after a run. Null for every other
    /// FilterType.</summary>
    public List<CustomerRefreshOutcome>? CustomerRefreshOutcomes { get; set; }
}

/// <summary>One row in the Admin app's Customer Refresh grid - see FilterType.
/// CustomerRefreshScan's doc comment. PortProDetails/SageDetails are both
/// formatted by Sage50ProfileFormatter.Describe so they read identically
/// side by side (currency=..., contact=..., address=..., phone=..., email=...).</summary>
public class CustomerRefreshCandidate
{
    public string PortProCustomerId { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;

    /// <summary>"INSERT" (no match found in Sage 50 by name) or "UPDATE" (a match
    /// was found).</summary>
    public string Operation { get; set; } = string.Empty;

    public string PortProDetails { get; set; } = string.Empty;

    /// <summary>The matched Sage 50 customer's own Name - null for an INSERT
    /// candidate (nothing matched).</summary>
    public string? SageCustomerName { get; set; }

    /// <summary>Sage 50's CURRENT profile for the matched customer, formatted the
    /// same way as PortProDetails for a direct side-by-side comparison - null for
    /// an INSERT candidate.</summary>
    public string? SageDetails { get; set; }

    /// <summary>The most recent Run Selected outcome for this customer, from
    /// SyncStateRepository's customer_refresh_status table (persisted, so this
    /// survives closing the app and re-Extracting) - null if this customer has
    /// never been run from the Customer Refresh tab. Overwritten every time it's
    /// selected and run again (see RecordCustomerRefreshOutcome's doc comment).
    /// Populated by CustomerSyncService.ScanForRefreshAsync so the grid shows it
    /// immediately after Extract, before anything is (re-)selected this session.</summary>
    public bool? LastOperationSuccess { get; set; }
    public DateTimeOffset? LastAppliedAtUtc { get; set; }
}

/// <summary>The result of actually processing one customer during a
/// FilterType.FullCustomerRefresh (EXECUTE) run - see CustomerSyncService.
/// ExecuteSelectedRefreshAsync. Matched back to its CustomerRefreshCandidate row
/// in the Admin app's grid by PortProCustomerId.</summary>
public class CustomerRefreshOutcome
{
    public string PortProCustomerId { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;

    /// <summary>"INSERT" or "UPDATE" - echoes which one was actually attempted,
    /// same values as CustomerRefreshCandidate.Operation.</summary>
    public string Operation { get; set; } = string.Empty;

    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTimeOffset AppliedAtUtc { get; set; }
}

public class InvoiceProcessingOutcome
{
    public string PortProInvoiceId { get; set; } = string.Empty;
    public string ReferenceNumber { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? Sage50InvoiceNumber { get; set; }
    public List<string> Messages { get; set; } = new();

    /// <summary>PortPro's own customer/company name for this invoice
    /// (invoice.Caller.CompanyName, falling back to invoice.CallerName) - shown as
    /// the first column of History &amp; Logs' "Validate Invoice Extracted" and
    /// "Invoice Transferred" grids. Populated for every outcome, including a
    /// failed-validation or not-found one, wherever the invoice/candidate carries
    /// enough PortPro data to know it; blank for a gap-fill candidate PortPro
    /// confirmed doesn't exist at all (nothing to name).</summary>
    public string? PortProCustomerName { get; set; }

    /// <summary>Copied from ValidationResult.CustomerAutoCreated - see that
    /// property's doc comment. Lets SyncOrchestrator.RunAsync's loop increment
    /// SyncResult.CustomersCreated without needing the ValidationResult itself.</summary>
    public bool CustomerAutoCreated { get; set; }

    /// <summary>"CREATED" if this invoice's own customer didn't exist in Sage 50
    /// and was just auto-created; "UPDATED" if the customer already existed and
    /// Sage50Settings.SyncCustomerUpdatesFromPortPro is on (meaning it's kept in
    /// sync with PortPro by the trailing incremental sweep - CustomerSyncService.
    /// SyncChangedCustomersAsync - not necessarily updated at this exact instant,
    /// since that sweep runs once per whole run, not per invoice); null if no
    /// customer was resolved at all (e.g. validation failed before reaching that
    /// point) or the setting is off for an existing customer. Shown as the
    /// "Sage50 Customer" column on History &amp; Logs' Invoice Transferred tab.</summary>
    public string? Sage50CustomerAction { get; set; }

    /// <summary>PortPro's billing/completed date for this invoice - populated for every
    /// outcome (success or failure), not just imported ones, so the Admin app's
    /// "Invoice Transferred" view can show it regardless.</summary>
    public DateTimeOffset? PortProInvoiceDate { get; set; }

    /// <summary>The date actually posted to Sage 50 (Sage50Invoice.InvoiceDate) - only
    /// set once the invoice is actually mapped/posted, so this is null for outcomes
    /// that failed validation before mapping was attempted.</summary>
    public DateTimeOffset? Sage50InvoiceDate { get; set; }

    /// <summary>Sage50InvoiceDate + the resolved NetTermDays (see SyncOrchestrator.
    /// ResolveNetTermDays) - the actual due date Sage 50 computes from the terms
    /// this app sets via SetTermDiscNetDay. Same null-for-failed-validation caveat
    /// as Sage50InvoiceDate above. Added 2026-08-22 alongside the due-date/terms
    /// fix so the resolved due date is visible, not just implied by NetTermDays.</summary>
    public DateTimeOffset? Sage50DueDate { get; set; }

    public decimal TotalAmount { get; set; }

    /// <summary>Sum of PortPro pricing lines recognized as a Canadian sales tax charge
    /// (see InvoiceValidationService.TryGetTaxAbbreviation) - the tax PortPro charged
    /// on this invoice, regardless of whether it mapped to a configured Sage 50 tax
    /// code.</summary>
    public decimal TaxCharged { get; set; }
}

/// <summary>Outcome of validating/matching one invoice against Sage 50 master data.</summary>
public class ValidationResult
{
    public bool IsValid => Errors.Count == 0;
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();

    public string? ResolvedSage50CustomerCode { get; set; }

    /// <summary>True only when this invoice's own validation is what actually
    /// created the Sage 50 customer just now (not when it resolved from the
    /// per-run cache or an existing Sage 50 match) - see InvoiceValidationService.
    /// ValidateCustomerAsync. Lets SyncOrchestrator.RunAsync count genuinely new
    /// customers created this run without double-counting every later invoice
    /// for that same (now-cached) customer.</summary>
    public bool CustomerAutoCreated { get; set; }

    public Dictionary<string, string> ResolvedItemCodesByChargeName { get; } = new();

    /// <summary>
    /// Per charge name, not a single shared value - confirmed live 2026-08-05 this
    /// was a real bug: a single ResolvedRevenueAccount set once (from whichever
    /// charge line happened to validate first) and reused as a fallback for every
    /// other line on the same invoice would silently give unrelated charges the
    /// wrong account whenever an invoice mixes charge types that resolve
    /// differently.
    /// </summary>
    public Dictionary<string, string> ResolvedRevenueAccountByChargeName { get; } = new();

    /// <summary>
    /// Sage 50 tax code (e.g. "H" for HST13%) resolved from a PortPro charge that
    /// matched Sage50Settings.TaxCodesByAbbreviation - applied to the invoice's
    /// revenue lines instead of importing the tax charge as its own line item.
    /// Null if no tax charge was present or none matched a configured mapping.
    /// </summary>
    public string? ResolvedTaxCode { get; set; }
}
