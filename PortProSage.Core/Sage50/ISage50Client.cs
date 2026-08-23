namespace PortProSage.Core.Sage50;

/// <summary>
/// Thrown by CreateInvoiceAsync when Sage 50 rejects a post because that exact
/// invoice number already exists for the customer - a real, correct validation
/// result (nothing was written), not evidence of a compromised SDK session.
/// Callers should treat this as "already imported" and continue, not fatal.
/// </summary>
public class DuplicateInvoiceNumberException : Exception
{
    public DuplicateInvoiceNumberException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Thrown by CreateInvoiceAsync when SelectAPARLedger can't resolve the
/// customer at all, even though InvoiceValidationService confirmed (or just
/// created) it earlier in this same run - the customer was deleted or renamed
/// directly in Sage 50 sometime in between. Confirmed live 2026-08-22 this is a
/// real risk specifically because of the per-run customer cache
/// (InvoiceValidationService._resolvedCustomerCodeByNameThisRun): a run can span
/// hours, and once a customer is cached as "found," nothing re-checks it again
/// for the rest of that run - without this exception, that stale assumption
/// would only surface as SelectAPARLedger failing deep inside CreateInvoiceAsync,
/// indistinguishable from a real compromised-session write failure.
///
/// Recoverable, not fatal: nothing was written to Sage 50 (SelectAPARLedger is a
/// pure selection call, not a write), so there's no reason to suspect a
/// compromised SDK session the way an actual failed Post() would. The caller
/// (SyncOrchestrator) fails just this one invoice, evicts the stale name from
/// the per-run cache so any LATER invoice for the same customer in this same run
/// gets a fresh look (and a real chance to auto-recreate the customer and
/// succeed immediately, not just via gap-fill), and leaves this invoice
/// unmarked - the automatic gap-fill sweep that already runs after every range-
/// based run (or the next Continue run) picks it up and processes it fresh.</summary>
public class CustomerNotFoundException : Exception
{
    public string CustomerCode { get; }

    public CustomerNotFoundException(string customerCode, string message) : base(message)
    {
        CustomerCode = customerCode;
    }
}

/// <summary>Thrown by CreateCustomerAsync/UpdateCustomerAsync when Sage 50 rejects
/// a specific profile field value outright (SimplySDK.InvalidEntryException) -
/// confirmed live 2026-08-23 for 'MANITOULIN GLOBAL FORWARDING TORONTO': PortPro's
/// billingEmail was "MGFPayables@MGFGroup.com,oceanimptor@mgfgroup.com,MGFCENTRAL@
/// mgfgroup.com," (74 characters, a comma-joined list of 3 addresses) and Sage 50's
/// Email field rejected it with "The string entered is too long. It must not be
/// longer than 50 characters." Before this exception existed, ANY exception from a
/// real write - including this one - was treated as evidence of a possibly-
/// compromised SDK session and terminated the whole process (see
/// TerminateOnFatalWriteError), which crashed two entire Full Customer Refresh runs
/// over a single customer's over-length email.
///
/// Recoverable, not fatal: the rejecting property setter (e.g. set_Email) throws
/// synchronously, before Save() is ever called - nothing was written. This is Sage
/// 50 correctly validating and refusing a bad value, exactly like
/// DuplicateInvoiceNumberException's "validation working correctly" case, not
/// evidence of a corrupted session. Callers should fail just this one
/// customer/invoice and continue, not the whole run. See
/// InvoiceValidationService.BuildSage50Profile's FirstEmail helper for the
/// matching root-cause fix (take just the first address from a multi-email
/// billingEmail, since a single address is virtually always well under Sage 50's
/// limit) - this exception remains the safety net for any other field/value Sage
/// 50 rejects that isn't specifically guarded against.</summary>
public class CustomerProfileRejectedException : Exception
{
    public CustomerProfileRejectedException(string message, Exception inner) : base(message, inner) { }
}

public class Sage50Customer
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? ReceivableAccount { get; set; }

    /// <summary>PortPro's caller.currency, e.g. "CAD" - confirmed live 2026-08-21
    /// this is sometimes blank (not every caller has one set in PortPro), in
    /// which case Sage50Client leaves the customer's currency at Sage 50's own
    /// default rather than writing an empty value.</summary>
    public string? CurrencyCode { get; set; }
}

/// <summary>Everything mappable from PortPro's full customer profile (see
/// PortProCustomer's doc comment) onto Sage 50's CustomerLedger - used for both
/// CreateCustomerAsync (a brand new customer) and UpdateCustomerAsync (an
/// existing one whose PortPro profile changed - see CustomerSyncService). Every
/// field here corresponds 1:1 to a real, confirmed-settable APARLedgerBase
/// property (reflected directly off the actual Sage 50 SDK assembly) - see
/// Sage50Client for the exact property each one writes to. Null/blank fields are
/// simply left unset rather than writing an empty value over whatever's there.</summary>
public class Sage50CustomerProfile
{
    public string Name { get; set; } = string.Empty;
    public string? CurrencyCode { get; set; }
    public string? Contact { get; set; }
    public string? Street1 { get; set; }
    public string? City { get; set; }
    public string? Province { get; set; }
    public string? Country { get; set; }
    public string? PostalCode { get; set; }
    public string? Phone1 { get; set; }
    public string? Email { get; set; }
}

/// <summary>Formats a Sage50CustomerProfile as one human-readable line - shared by
/// Sage50Client (DRY RUN log lines) and CustomerSyncService.ScanForRefreshAsync
/// (the Admin app's Customer Refresh grid, where the exact same format is used
/// for both the PortPro-incoming and Sage50-current columns so they read as a
/// direct side-by-side comparison).</summary>
public static class Sage50ProfileFormatter
{
    public static string Describe(Sage50CustomerProfile p) =>
        $"currency={p.CurrencyCode ?? "(none)"}, contact={p.Contact ?? "(none)"}, address={p.Street1 ?? "(none)"}, " +
        $"{p.City ?? "(none)"}, {p.Province ?? "(none)"}, {p.Country ?? "(none)"}, {p.PostalCode ?? "(none)"}, " +
        $"phone={p.Phone1 ?? "(none)"}, email={p.Email ?? "(none)"}";
}

public class Sage50Item
{
    public string Code { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? RevenueAccount { get; set; }
    public bool IsService { get; set; } = true;
}

public class Sage50InvoiceLine
{
    public string ItemCode { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }
    public string RevenueAccount { get; set; } = string.Empty;
    public string? TaxCode { get; set; }
}

public class Sage50Invoice
{
    /// <summary>PortPro reference number, stored on the Sage invoice for traceability/idempotency.</summary>
    public string ExternalReference { get; set; } = string.Empty;
    public string CustomerCode { get; set; } = string.Empty;
    public DateTime InvoiceDate { get; set; }
    public List<Sage50InvoiceLine> Lines { get; set; } = new();

    /// <summary>Number of days from InvoiceDate to due date - drives Sage 50's
    /// SalesJournal.SetTermDiscNetDay, the field that was never being set at all
    /// before this (every invoice posted with an implicit Net 0, so Due Date =
    /// Invoice Date regardless of the customer's real terms). Resolved by
    /// SyncOrchestrator.MapToSage50Invoice from PortPro's own per-invoice
    /// payment_terms when available (confirmed live 2026-08-21 this is present
    /// and genuinely per-invoice, not a constant), falling back to
    /// Sage50Settings.DefaultNetTermDays otherwise.</summary>
    public int NetTermDays { get; set; }
}

/// <summary>
/// Abstraction over the Sage 50 Canadian Edition SDK so the rest of the
/// application never talks COM directly. Implement/adjust Sage50Client against
/// the exact object model of the SDK version installed on this server.
/// </summary>
public interface ISage50Client : IDisposable
{
    Task ConnectAsync(CancellationToken ct);

    Task<Sage50Customer?> FindCustomerByNameAsync(string name, CancellationToken ct);
    Task<Sage50Customer> CreateCustomerAsync(Sage50CustomerProfile profile, string receivableAccount, CancellationToken ct);

    /// <summary>Reads back a matched Sage 50 customer's CURRENT full profile
    /// (every ApplyProfile-mapped field) - used only for display, by
    /// CustomerSyncService.ScanForRefreshAsync's Admin-app preview grid, so the
    /// operator can see what's actually in Sage 50 today before choosing to
    /// overwrite it. Null if no customer matches by name.</summary>
    Task<Sage50CustomerProfile?> GetCustomerProfileByNameAsync(string name, CancellationToken ct);

    /// <summary>Updates an already-existing Sage 50 customer's profile fields -
    /// used by CustomerSyncService when PortPro's own record has changed since
    /// this customer was last synced. Throws if no customer matches profile.Name
    /// (callers are expected to have already confirmed it exists via
    /// FindCustomerByNameAsync before calling this).</summary>
    Task UpdateCustomerAsync(Sage50CustomerProfile profile, CancellationToken ct);

    Task<Sage50Item?> FindItemByCodeOrDescriptionAsync(string codeOrDescription, CancellationToken ct);
    Task<Sage50Item> CreateServiceItemAsync(string code, string description, string revenueAccount, CancellationToken ct);

    Task<bool> AccountExistsAsync(string accountNumber, CancellationToken ct);

    /// <summary>Returns true if an invoice with this external reference was already imported.</summary>
    Task<bool> InvoiceAlreadyExistsAsync(string externalReference, CancellationToken ct);

    /// <summary>Creates the sales invoice in Sage 50 and returns the Sage-assigned invoice number.</summary>
    Task<string> CreateInvoiceAsync(Sage50Invoice invoice, CancellationToken ct);
}
