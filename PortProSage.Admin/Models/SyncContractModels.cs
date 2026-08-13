namespace PortProSage.Admin.Models;

// Mirrors PortProSage.Core.Models.SyncModels.cs exactly (property names, enum
// order) - this app writes *.request.json / reads *.result.json in the same
// TriggerFolder the Service's Worker already watches, using plain
// System.Text.Json.JsonSerializer with default options (matching how
// TriggerFileManager.cs on the Service side serializes), so the JSON shape
// must match byte-for-byte. This project does NOT reference PortProSage.Core
// directly (that would drag in the net48/x86/Sage 50 SDK dependency chain for
// an app that never touches Sage 50) - these are deliberately independent
// copies of just the JSON contract.

public enum FilterType
{
    LastChangedDate,
    InvoiceNumberRange,
    CompletedDateRange,

    // Appended at the end, exactly matching PortProSage.Core.Models.SyncModels.cs's
    // ordering - System.Text.Json's default enum handling serializes by NUMERIC
    // value, not name, so this ordinal position must match byte-for-byte or the
    // Service would deserialize a request file written by this app as the wrong
    // filter type entirely.
    InvoiceNumberList
}

public class SyncRequest
{
    public string RequestId { get; set; } = Guid.NewGuid().ToString("N");
    public FilterType FilterType { get; set; }

    public DateTimeOffset? From { get; set; }
    public DateTimeOffset? To { get; set; }

    public string? StartInvoiceNumber { get; set; }
    public string? EndInvoiceNumber { get; set; }
    public string? InvoiceNumberList { get; set; }

    public bool UseWatermark { get; set; }
    public int? MaxInvoicesToProcess { get; set; }

    public DateTimeOffset RequestedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string RequestedBy { get; set; } = "PortProSage.Admin";
}

public class SyncResult
{
    public string RequestId { get; set; } = string.Empty;
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset FinishedAtUtc { get; set; }
    public int ProcessId { get; set; }
    public bool Skipped { get; set; }
    public string? SkipReason { get; set; }
    public int InvoicesFetched { get; set; }
    public int InvoicesImported { get; set; }
    public int InvoicesSkippedAlreadyImported { get; set; }
    public int InvoicesSkippedZeroOrNegativeAmount { get; set; }
    public int InvoicesSkippedBeforeCutoff { get; set; }
    public int InvoicesFailedValidation { get; set; }
    public int InvoicesFailedImport { get; set; }

    /// <summary>False for a progress checkpoint written while the run is still in
    /// progress; true only on the final write. Mirrors Core's SyncResult.IsFinal -
    /// see that doc comment for why this exists (interrupted runs used to leave no
    /// result file at all, so counts showed blank in History &amp; Logs).</summary>
    public bool IsFinal { get; set; }

    /// <summary>The actual invoice-date window this run covered - mirrors Core's
    /// SyncResult.EffectiveFromUtc/EffectiveToUtc. Null for Invoice number range
    /// mode (no date concept) or for a historical entry recorded before this
    /// field existed. This is what History &amp; Logs' "Inv Start Date"/"Inv End
    /// Date" columns and the Previous Run section show - not the watermark, which
    /// is only meaningful for a watermark-driven run.</summary>
    public DateTimeOffset? EffectiveFromUtc { get; set; }
    public DateTimeOffset? EffectiveToUtc { get; set; }

    /// <summary>How many day-sized batches this run split into - mirrors Core's
    /// SyncResult.BatchCount. 0 for a historical entry recorded before this field
    /// existed, or a run that never started (e.g. Skipped).</summary>
    public int BatchCount { get; set; }

    public List<InvoiceProcessingOutcome> Outcomes { get; set; } = new();

    /// <summary>Pre/post snapshot of the persisted "continue from" state - see
    /// SyncResult.WatermarkBeforeRun's doc comment on the Core side. Always
    /// populated regardless of UseWatermark; Before == After confirms an
    /// explicit-range run genuinely left persisted state untouched.</summary>
    public DateTimeOffset? WatermarkBeforeRun { get; set; }
    public DateTimeOffset? WatermarkAfterRun { get; set; }
    public string? LastProcessedInvoiceNumberBeforeRun { get; set; }
    public string? LastProcessedInvoiceNumberAfterRun { get; set; }
}

public class InvoiceProcessingOutcome
{
    public string PortProInvoiceId { get; set; } = string.Empty;
    public string ReferenceNumber { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? Sage50InvoiceNumber { get; set; }
    public List<string> Messages { get; set; } = new();

    public DateTimeOffset? PortProInvoiceDate { get; set; }
    public DateTimeOffset? Sage50InvoiceDate { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal TaxCharged { get; set; }
}
