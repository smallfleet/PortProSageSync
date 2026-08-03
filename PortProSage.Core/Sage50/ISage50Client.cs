namespace PortProSage.Core.Sage50;

public class Sage50Customer
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? ReceivableAccount { get; set; }
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
    Task<Sage50Customer> CreateCustomerAsync(string name, string receivableAccount, CancellationToken ct);

    Task<Sage50Item?> FindItemByCodeOrDescriptionAsync(string codeOrDescription, CancellationToken ct);
    Task<Sage50Item> CreateServiceItemAsync(string code, string description, string revenueAccount, CancellationToken ct);

    Task<bool> AccountExistsAsync(string accountNumber, CancellationToken ct);

    /// <summary>Returns true if an invoice with this external reference was already imported.</summary>
    Task<bool> InvoiceAlreadyExistsAsync(string externalReference, CancellationToken ct);

    /// <summary>Creates the sales invoice in Sage 50 and returns the Sage-assigned invoice number.</summary>
    Task<string> CreateInvoiceAsync(Sage50Invoice invoice, CancellationToken ct);
}
