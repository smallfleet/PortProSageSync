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
    InvoiceNumberList
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

    /// <summary>
    /// Caps how many eligible (amount > 0) invoices this run will actually process,
    /// regardless of how many fall within the fetched range - confirmed live
    /// 2026-08-05 that computing a Start/End boundary from a separate "find N
    /// candidates" snapshot and handing it to a later, independently-refetched run
    /// doesn't reliably cap anything: PortPro is live data, so the two fetches can
    /// disagree, and the second run just processes everything in the boundary
    /// regardless of the original N. Enforcing the cap inside the same run's own
    /// loop is the only way it's actually a cap. Null means unlimited (the default
    /// for automatic polling and ordinary manual triggers).
    /// </summary>
    public int? MaxInvoicesToProcess { get; set; }

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

    /// <summary>Invoices whose own date (BillingDate, falling back to
    /// CompletedDate) fell before SyncSettings.CutoffInvoiceDate - see that
    /// property's doc comment for why this exists.</summary>
    public int InvoicesSkippedBeforeCutoff { get; set; }

    public int InvoicesFailedValidation { get; set; }
    public int InvoicesFailedImport { get; set; }
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

    /// <summary>How many day-sized batches this run split into - see
    /// SyncSettings.SplitRunByDay's doc comment. Always at least 1 (a run that
    /// isn't split, or has no date range at all, is still "1 batch" - recorded
    /// here and logged the same way as a genuinely multi-batch run, not treated
    /// as a special case). 0 only for a run that never even started (e.g. the
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
}

public class InvoiceProcessingOutcome
{
    public string PortProInvoiceId { get; set; } = string.Empty;
    public string ReferenceNumber { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? Sage50InvoiceNumber { get; set; }
    public List<string> Messages { get; set; } = new();

    /// <summary>PortPro's billing/completed date for this invoice - populated for every
    /// outcome (success or failure), not just imported ones, so the Admin app's
    /// "Invoice Transferred" view can show it regardless.</summary>
    public DateTimeOffset? PortProInvoiceDate { get; set; }

    /// <summary>The date actually posted to Sage 50 (Sage50Invoice.InvoiceDate) - only
    /// set once the invoice is actually mapped/posted, so this is null for outcomes
    /// that failed validation before mapping was attempted.</summary>
    public DateTimeOffset? Sage50InvoiceDate { get; set; }

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
