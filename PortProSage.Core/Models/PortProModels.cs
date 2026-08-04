using System.Text.Json.Serialization;

namespace PortProSage.Core.Models;

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
