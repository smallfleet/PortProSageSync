namespace PortProSage.Core.Models;

public enum FilterType
{
    /// <summary>Pull invoices whose PortPro "last changed" (updatedAt) falls in [From, To].</summary>
    LastChangedDate,

    /// <summary>Pull invoices whose reference/invoice number falls in [StartNumber, EndNumber] (inclusive).</summary>
    InvoiceNumberRange,

    /// <summary>Pull invoices whose load completed date falls in [From, To].</summary>
    CompletedDateRange
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

    public DateTimeOffset RequestedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string RequestedBy { get; set; } = Environment.UserName;
}

public class SyncResult
{
    public string RequestId { get; set; } = string.Empty;
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset FinishedAtUtc { get; set; }
    public int InvoicesFetched { get; set; }
    public int InvoicesImported { get; set; }
    public int InvoicesSkippedAlreadyImported { get; set; }
    public int InvoicesFailedValidation { get; set; }
    public int InvoicesFailedImport { get; set; }
    public List<InvoiceProcessingOutcome> Outcomes { get; set; } = new();
}

public class InvoiceProcessingOutcome
{
    public string PortProInvoiceId { get; set; } = string.Empty;
    public string ReferenceNumber { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? Sage50InvoiceNumber { get; set; }
    public List<string> Messages { get; set; } = new();
}

/// <summary>Outcome of validating/matching one invoice against Sage 50 master data.</summary>
public class ValidationResult
{
    public bool IsValid => Errors.Count == 0;
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();

    public string? ResolvedSage50CustomerCode { get; set; }
    public Dictionary<string, string> ResolvedItemCodesByChargeName { get; } = new();
    public string? ResolvedRevenueAccount { get; set; }

    /// <summary>
    /// Sage 50 tax code (e.g. "H" for HST13%) resolved from a PortPro charge that
    /// matched Sage50Settings.TaxCodesByAbbreviation - applied to the invoice's
    /// revenue lines instead of importing the tax charge as its own line item.
    /// Null if no tax charge was present or none matched a configured mapping.
    /// </summary>
    public string? ResolvedTaxCode { get; set; }
}
