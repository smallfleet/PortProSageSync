using System.Text.Json.Serialization;

namespace PortProSage.Core.Models;

/// <summary>Result of PortProClient.GetInvoicesAsync - the invoices actually found,
/// plus how many requested candidates came back "not found" (404). NotFoundCount is
/// only ever non-zero for FilterType.InvoiceNumberList/InvoiceNumberGapScan, which
/// are the only modes that check an EXPLICIT list of candidates one at a time and so
/// are the only ones that can know a specific reference number didn't exist - a list-
/// endpoint-driven mode (date range, invoice number range) has no such concept, since
/// it only ever sees what PortPro chose to return.</summary>
public class PortProFetchResult
{
    public List<PortProInvoice> Invoices { get; set; } = new();
    public int NotFoundCount { get; set; }

    /// <summary>The actual reference numbers behind NotFoundCount - added 2026-08-24
    /// so SyncOrchestrator can record a proper per-invoice Outcome (Success=false,
    /// with a clear message) for each one, instead of only a bare count with no way
    /// to see which candidates those were in History &amp; Logs' "Validate Invoice
    /// Extracted" grid.</summary>
    public List<string> NotFoundReferenceNumbers { get; set; } = new();
}

/// <summary>
/// Mirrors the invoice object returned by PortPro's GET /invoices endpoints.
/// Field names follow the demo payload published in PortPro's API reference;
/// double-check against your account's actual response before going live,
/// since PortPro has been known to add fields (e.g. updatedAt/createdAt) over time.
/// </summary>
public class PortProInvoice
{
    /// <summary>
    /// Not present on the wire at this level (see PortProLoadEnvelope) - populated
    /// by PortProClient.GetInvoicesAsync from the enclosing envelope's "_id" after
    /// deserialization. Left as a JsonPropertyName mapping too in case a future
    /// API version moves it here directly.
    /// </summary>
    [JsonPropertyName("_id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("load_reference_number")]
    public string LoadReferenceNumber { get; set; } = string.Empty;

    [JsonPropertyName("reference_number")]
    public string ReferenceNumber { get; set; } = string.Empty;

    /// <summary>PortPro invoice / charge status, e.g. BILLING, PARTIALLY_PAID, FULL_PAID.</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("totalAmount")]
    public decimal TotalAmount { get; set; }

    [JsonPropertyName("paidAmount")]
    public decimal PaidAmount { get; set; }

    [JsonPropertyName("remainAmount")]
    public decimal RemainAmount { get; set; }

    [JsonPropertyName("billingDate")]
    public DateTimeOffset? BillingDate { get; set; }

    /// <summary>
    /// NOT observed in live PortPro responses as of 2026-08-04 (only billingDate,
    /// createdAt, updatedAt were present on real invoices). Kept as an optional
    /// field in case some invoices carry it; the "invoice complete date range"
    /// filter actually queries PortPro's confirmed billingFrom/billingTo params
    /// against billingDate - see PortProClient.BuildQueryString.
    /// </summary>
    [JsonPropertyName("completedDate")]
    public DateTimeOffset? CompletedDate { get; set; }

    /// <summary>Not present on the wire at this level - see Id's comment above.</summary>
    [JsonPropertyName("createdAt")]
    public DateTimeOffset? CreatedAt { get; set; }

    /// <summary>
    /// Last modified timestamp - used for the "last changed date" filter. Not
    /// present on the wire at this level - see Id's comment above.
    /// </summary>
    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; set; }

    [JsonPropertyName("caller")]
    public PortProCaller? Caller { get; set; }

    [JsonPropertyName("callerName")]
    public string? CallerName { get; set; }

    [JsonPropertyName("pricing")]
    public List<PortProPricingLine> Pricing { get; set; } = new();

    [JsonPropertyName("referenceFields")]
    public Dictionary<string, string>? ReferenceFields { get; set; }

    /// <summary>Not present on the wire at this level - lives on the enclosing
    /// PortProLoadEnvelope ("payment_terms"/"payment_terms_method"), same as Id/
    /// CreatedAt/UpdatedAt above - populated by PortProClient's flattening (both
    /// GetInvoicesAsync and GetInvoiceAsync/ConsolidateChargeSets). See
    /// PortProLoadEnvelope.PaymentTerms's doc comment for how this was confirmed.</summary>
    public int? PaymentTermsNetDays { get; set; }
    public string? PaymentTermsMethod { get; set; }
}

public class PortProCaller
{
    [JsonPropertyName("_id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("company_name")]
    public string CompanyName { get; set; } = string.Empty;

    [JsonPropertyName("currency")]
    public string? Currency { get; set; }
}

public class PortProPricingLine
{
    [JsonPropertyName("chargeType")]
    public string ChargeType { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("unit")]
    public string? Unit { get; set; }

    [JsonPropertyName("finalAmount")]
    public string FinalAmount { get; set; } = "0";

    /// <summary>
    /// Unlike finalAmount (a quoted string, e.g. "300.00"), the real API returns
    /// this as a bare JSON number - confirmed live 2026-08-04 via a JsonException
    /// ("Cannot get the value of a token type 'Number' as a string") once actual
    /// production invoices flowed through (the earlier 0-invoice test windows
    /// never hit this). Not currently used elsewhere in the codebase (business
    /// logic uses FinalAmount), kept for completeness.
    /// </summary>
    [JsonPropertyName("amount")]
    public decimal? Amount { get; set; }

    [JsonPropertyName("glCode")]
    public string? GlCode { get; set; }
}

/// <summary>
/// Envelope PortPro wraps list responses in. Confirmed 2026-08-04 against the
/// live API using the production connector's own credentials: top-level array
/// key is "data" (not "invoice"), total count key is "count" (not "total").
/// </summary>
public class PortProInvoiceListResponse
{
    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("data")]
    public List<PortProLoadEnvelope> Data { get; set; } = new();
}

/// <summary>
/// Each element of the list response's "data" array is NOT itself a flat
/// invoice - confirmed live 2026-08-04 (a 3080-invoice dry run came back with
/// every single ReferenceNumber/Caller/Pricing empty until this was fixed). The
/// real shape wraps one load: "_id"/"createdAt"/"updatedAt" live here, while
/// reference_number/pricing/caller/billingDate/etc. live one level deeper, in
/// "invoice" (an array - only ever observed with exactly one element so far,
/// but modeled as a list since nothing in the payload guarantees exactly one).
/// PortProClient.GetInvoicesAsync flattens this into plain PortProInvoice
/// objects so nothing downstream (orchestrator, validator, state tracking)
/// needs to know about this wrapper.
/// </summary>
public class PortProLoadEnvelope
{
    [JsonPropertyName("_id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("invoice")]
    public List<PortProInvoice> Invoice { get; set; } = new();

    [JsonPropertyName("createdAt")]
    public DateTimeOffset? CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>Confirmed live 2026-08-21 (fetched real invoices directly against
    /// production, both the list and single-invoice endpoints) - this invoice's
    /// actual payment terms, e.g. 30. Lives at this envelope level (a sibling of
    /// "invoice", not inside it), and was present on 100/100 invoices sampled -
    /// genuinely per-invoice, not a constant (both 26 and 30 were observed in the
    /// same sample). This is what drives the Sage 50 due-date fix - see
    /// PortProInvoice.PaymentTermsNetDays/PaymentTermsMethod and
    /// SyncOrchestrator.MapToSage50Invoice.</summary>
    [JsonPropertyName("payment_terms")]
    public int? PaymentTerms { get; set; }

    /// <summary>The unit PaymentTerms is expressed in - every sample observed
    /// live was "day", but nothing in the payload guarantees that's the only
    /// value PortPro ever sends, so it's captured rather than assumed.</summary>
    [JsonPropertyName("payment_terms_method")]
    public string? PaymentTermsMethod { get; set; }

    /// <summary>PortPro's own computed due date - not currently used for anything
    /// (PaymentTerms/PaymentTermsMethod is what actually drives the Sage 50 fix),
    /// kept only as an audit/cross-check field since it was right there in the
    /// same confirmed-live response.</summary>
    [JsonPropertyName("invoiceDueDate")]
    public DateTimeOffset? InvoiceDueDate { get; set; }
}

/// <summary>
/// Wraps PortPro's single-invoice endpoint response (GET /invoices/{referenceNumber}) -
/// confirmed live 2026-08-12 this is a DIFFERENT shape than the list endpoint's bare
/// array-of-envelopes: everything sits under "data" (itself shaped just like one
/// PortProLoadEnvelope), alongside a sibling "error" field. Real example: a single
/// reference number ("RSRE_000284") can itself contain MULTIPLE "invoice" array
/// entries - one per charge set, each from a DIFFERENT load (RSRUSH_E100718 and
/// RSRUSH_E100725 in that example) - which is exactly what PortPro's own UI shows
/// as "N Charge Sets Invoiced". PortProClient.GetInvoiceAsync consolidates them into
/// one PortProInvoice (combined pricing lines, summed totals) rather than returning
/// them as separate invoices under the same reference number.
/// </summary>
public class PortProSingleInvoiceResponse
{
    [JsonPropertyName("data")]
    public PortProLoadEnvelope? Data { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

/// <summary>Confirmed live 2026-08-21 by calling GET /v1/customer/{id} directly
/// against production - a full, separate customer profile object PortPro keeps,
/// distinct from the lightweight PortProCaller embedded on each invoice (which
/// only ever carries _id/company_name/currency/externalSystemID). The list form
/// (GET /v1/customer, no id) returns the exact same shape for every item - 94
/// fields either way, confirmed by diffing the two - so PortProClient.
/// GetAllCustomersAsync (used for the periodic change-detection sweep) needs no
/// separate per-customer detail call.
///
/// Real values checked on 2 live customers: address1/city/state/country/zip_code
/// are populated; billingEmail is populated (and is the genuinely useful email -
/// the bare "email" field is a PortPro-generated proxy address like
/// "q9ki9kdn3fjv3zf@portpro.io", not a real contact address); main_contact_name/
/// secondary_contact_name/mobile/secondaryPhoneNo were null/empty on both
/// customers checked (the fields exist in PortPro's data model, just unused by
/// this account so far - mapped anyway since the point is capturing whatever's
/// there, now or later). defaultPaymentTerms.days matched the per-invoice
/// payment_terms seen on that customer's own invoices exactly (30), confirming
/// it's the authoritative source, not the top-level "payment_terms" field (which
/// was 0 on both customers checked - a different, evidently unused field).</summary>
public class PortProCustomer
{
    [JsonPropertyName("_id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("company_name")]
    public string CompanyName { get; set; } = string.Empty;

    [JsonPropertyName("address1")]
    public string? Address1 { get; set; }

    [JsonPropertyName("city")]
    public string? City { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("country")]
    public string? Country { get; set; }

    [JsonPropertyName("zip_code")]
    public string? ZipCode { get; set; }

    [JsonPropertyName("main_contact_name")]
    public string? MainContactName { get; set; }

    [JsonPropertyName("secondary_contact_name")]
    public string? SecondaryContactName { get; set; }

    /// <summary>PortPro-generated proxy address (e.g. "q9ki9kdn3fjv3zf@portpro.io"),
    /// NOT a real contact email - see BillingEmail for the one actually worth
    /// mapping to Sage 50.</summary>
    [JsonPropertyName("email")]
    public string? Email { get; set; }

    /// <summary>The real, human-entered contact email(s) - confirmed live, can be
    /// comma-separated with multiple addresses (e.g. "a@x.com,b@x.com,"). Sage 50's
    /// Email field can only hold one address and has its own 50-character hard
    /// limit (confirmed live 2026-08-23 via a real SimplySDK.InvalidEntryException
    /// that crashed two Full Customer Refresh runs before this was fixed) - see
    /// InvoiceValidationService.BuildSage50Profile's FirstEmail helper, which takes
    /// just the first address rather than passing this through unsplit.</summary>
    [JsonPropertyName("billingEmail")]
    public string? BillingEmail { get; set; }

    [JsonPropertyName("mobile")]
    public string? Mobile { get; set; }

    [JsonPropertyName("secondaryPhoneNo")]
    public string? SecondaryPhoneNo { get; set; }

    [JsonPropertyName("currency")]
    public string? Currency { get; set; }

    [JsonPropertyName("defaultPaymentTerms")]
    public PortProCustomerPaymentTerms? DefaultPaymentTerms { get; set; }

    /// <summary>Drives the automatic customer-sync feature (SyncOrchestrator's
    /// periodic CustomerSyncService) - compared against the last-synced value
    /// recorded in SyncStateRepository's customer_sync_state table to detect a
    /// change since this customer was last pushed into Sage 50.</summary>
    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; set; }
}

public class PortProCustomerPaymentTerms
{
    [JsonPropertyName("paymentTermsMethod")]
    public string? PaymentTermsMethod { get; set; }

    [JsonPropertyName("days")]
    public int? Days { get; set; }
}

/// <summary>Wraps GET /v1/customer/{id} - single-customer fetch.</summary>
public class PortProCustomerSingleResponse
{
    [JsonPropertyName("data")]
    public PortProCustomer? Data { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

/// <summary>Wraps GET /v1/customer (no id) - the full customer list, paginated the
/// same way as the invoice list endpoint (skip/limit). Confirmed live 2026-08-21:
/// 223 total customers on this account - small enough that PortProClient.
/// GetAllCustomersAsync just fetches the whole thing on every periodic sync
/// sweep rather than needing an incremental/date-filtered query.</summary>
public class PortProCustomerListResponse
{
    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("data")]
    public List<PortProCustomer> Data { get; set; } = new();
}

/// <summary>
/// Envelope returned by GET /generate-new-token. Confirmed 2026-08-04 live:
/// <c>{"_object":..., "self":..., "version":..., "data": {"token":..., "refresh_token":..., "tokenType":"public"}, "error": null}</c>
/// </summary>
public class PortProTokenEnvelope
{
    [JsonPropertyName("data")]
    public PortProTokenData? Data { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public class PortProTokenData
{
    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; set; } = string.Empty;

    [JsonPropertyName("tokenType")]
    public string TokenType { get; set; } = string.Empty;
}
