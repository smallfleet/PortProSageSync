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

    /// <summary>Load/invoice completion date - used for the "invoice complete date range" filter.</summary>
    [JsonPropertyName("completedDate")]
    public DateTimeOffset? CompletedDate { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset? CreatedAt { get; set; }

    /// <summary>Last modified timestamp - used for the "last changed date" filter.</summary>
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

    [JsonPropertyName("amount")]
    public string? Amount { get; set; }

    [JsonPropertyName("glCode")]
    public string? GlCode { get; set; }
}

/// <summary>Envelope PortPro wraps list responses in.</summary>
public class PortProInvoiceListResponse
{
    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("invoice")]
    public List<PortProInvoice> Invoice { get; set; } = new();
}

public class PortProTokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; set; } = string.Empty;

    [JsonPropertyName("expires_in")]
    public int ExpiresInSeconds { get; set; }
}
