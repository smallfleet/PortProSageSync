using Microsoft.Extensions.Logging;
using PortProSage.Core.Config;
using PortProSage.Core.Models;
using PortProSage.Core.Sage50;

namespace PortProSage.Core.Validation;

/// <summary>
/// Validates a PortPro invoice against Sage 50 master data (customer, items/services,
/// GL accounts) before import, auto-creating missing customers/items when configured
/// to do so (per Sage50Settings.AutoCreateCustomers / AutoCreateItems).
/// </summary>
public class InvoiceValidationService
{
    private readonly ISage50Client _sage50;
    private readonly Sage50Settings _settings;
    private readonly ILogger<InvoiceValidationService> _logger;

    public InvoiceValidationService(ISage50Client sage50, Sage50Settings settings, ILogger<InvoiceValidationService> logger)
    {
        _sage50 = sage50;
        _settings = settings;
        _logger = logger;
    }

    public async Task<ValidationResult> ValidateAsync(PortProInvoice invoice, CancellationToken ct)
    {
        var result = new ValidationResult();

        await ValidateCustomerAsync(invoice, result, ct);
        await ValidateChargeLinesAsync(invoice, result, ct);

        if (invoice.Pricing.Count == 0)
        {
            result.Errors.Add("Invoice has no pricing/charge lines to import.");
        }
        else if (result.ResolvedItemCodesByChargeName.Count == 0 && result.ResolvedTaxCode is not null)
        {
            result.Errors.Add("Invoice's only charge(s) resolved as tax, with no revenue line for Sage 50 to apply the tax code to.");
        }

        return result;
    }

    private async Task ValidateCustomerAsync(PortProInvoice invoice, ValidationResult result, CancellationToken ct)
    {
        var customerName = invoice.Caller?.CompanyName ?? invoice.CallerName;

        if (string.IsNullOrWhiteSpace(customerName))
        {
            result.Errors.Add("PortPro invoice has no caller/customer name to match against Sage 50.");
            return;
        }

        var existing = await _sage50.FindCustomerByNameAsync(customerName, ct);
        if (existing is not null)
        {
            result.ResolvedSage50CustomerCode = existing.Code;
            return;
        }

        if (!_settings.AutoCreateCustomers)
        {
            result.Errors.Add($"Customer '{customerName}' not found in Sage 50 and auto-create is disabled.");
            return;
        }

        try
        {
            var created = await _sage50.CreateCustomerAsync(customerName, _settings.DefaultReceivableAccount, ct);
            result.ResolvedSage50CustomerCode = created.Code;
            result.Warnings.Add($"Customer '{customerName}' did not exist in Sage 50 and was auto-created (code {created.Code}).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to auto-create Sage 50 customer '{Customer}'", customerName);
            result.Errors.Add($"Failed to auto-create customer '{customerName}': {ex.Message}");
        }
    }

    private async Task ValidateChargeLinesAsync(PortProInvoice invoice, ValidationResult result, CancellationToken ct)
    {
        foreach (var line in invoice.Pricing)
        {
            if (string.IsNullOrWhiteSpace(line.Name))
            {
                result.Errors.Add("A charge line is missing a name/description.");
                continue;
            }

            if (TryGetTaxAbbreviation(line.Name, out var taxAbbreviation))
            {
                if (_settings.TaxCodesByAbbreviation.TryGetValue(taxAbbreviation, out var taxCode))
                {
                    // Resolved: this charge isn't a real line item - it's applied to the
                    // invoice's revenue lines as a Sage 50 tax code instead (see
                    // Sage50Settings.TaxCodesByAbbreviation), so Sage 50 calculates and
                    // posts the tax itself. Skip item/account resolution for this line.
                    if (result.ResolvedTaxCode is not null && result.ResolvedTaxCode != taxCode)
                    {
                        result.Warnings.Add(
                            $"Invoice has multiple different tax charges ('{result.ResolvedTaxCode}' and '{taxCode}') - " +
                            "only the first was applied; this combination hasn't been seen before and needs manual review.");
                    }
                    else
                    {
                        result.ResolvedTaxCode ??= taxCode;
                    }

                    continue;
                }

                var message = $"Charge '{line.Name}' looks like a {taxAbbreviation} tax line, but no Sage 50 tax code " +
                               $"is configured for '{taxAbbreviation}' in Sage50Settings.TaxCodesByAbbreviation - it will " +
                               "be imported as an ordinary service item/revenue line instead of through Sage 50's real " +
                               "tax mechanism. Add a mapping (see Setup > Settings > Company > Sales Taxes > Tax Codes " +
                               "in Sage 50) once you know the right code.";
                result.Warnings.Add(message);
                _logger.LogWarning("Invoice {Ref}: {Message}", invoice.ReferenceNumber, message);
            }

            var existingItem = await _sage50.FindItemByCodeOrDescriptionAsync(line.Name, ct);
            string revenueAccount;

            if (existingItem is not null)
            {
                revenueAccount = !string.IsNullOrWhiteSpace(existingItem.RevenueAccount)
                    ? existingItem.RevenueAccount
                    : ResolveFallbackAccount(line);

                result.ResolvedItemCodesByChargeName[line.Name] = existingItem.Code;
            }
            else if (_settings.AutoCreateItems)
            {
                try
                {
                    var code = MakeItemCode(line.Name);
                    revenueAccount = ResolveFallbackAccount(line);

                    var created = await _sage50.CreateServiceItemAsync(code, line.Name, revenueAccount, ct);
                    result.ResolvedItemCodesByChargeName[line.Name] = created.Code;
                    result.Warnings.Add($"Service item '{line.Name}' did not exist in Sage 50 and was auto-created (code {created.Code}).");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to auto-create Sage 50 item for charge '{Charge}'", line.Name);
                    result.Errors.Add($"Failed to auto-create item for charge '{line.Name}': {ex.Message}");
                    continue;
                }
            }
            else
            {
                result.Errors.Add($"Charge '{line.Name}' has no matching Sage 50 item/service and auto-create is disabled.");
                continue;
            }

            revenueAccount = ResolveFallbackAccount(line);
            if (!await _sage50.AccountExistsAsync(revenueAccount, ct))
            {
                result.Errors.Add($"Revenue account '{revenueAccount}' for charge '{line.Name}' does not exist in Sage 50's chart of accounts.");
                continue;
            }

            result.ResolvedRevenueAccount ??= revenueAccount;
        }
    }

    private string ResolveFallbackAccount(PortProPricingLine line)
    {
        // PortPro charge lines can carry their own glCode (see the sample invoice payload);
        // prefer that, and fall back to the configured default revenue account.
        return !string.IsNullOrWhiteSpace(line.GlCode) ? line.GlCode! : _settings.DefaultRevenueAccount;
    }

    private static string MakeItemCode(string chargeName)
    {
        var cleaned = new string(chargeName.Where(char.IsLetterOrDigit).ToArray());
        return cleaned.Length <= 12 ? cleaned.ToUpperInvariant() : cleaned.Substring(0, 12).ToUpperInvariant();
    }

    private static readonly System.Text.RegularExpressions.Regex TaxChargeNamePattern =
        new(@"\b(HST|GST|PST|QST)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Matches against the charge NAME (e.g. "HST (13 %)"), not the "description"
    /// field - description turned out to be free-text notes (locations, timing
    /// details) with no reliable structure, confirmed 2026-08-04 against real data;
    /// the Canadian tax abbreviation showing up in the charge name itself is the
    /// only consistent signal PortPro provides. Returns the abbreviation in
    /// upper-case (e.g. "HST") for use as a Sage50Settings.TaxCodesByAbbreviation key.
    /// </summary>
    private static bool TryGetTaxAbbreviation(string chargeName, out string abbreviation)
    {
        var match = TaxChargeNamePattern.Match(chargeName);
        abbreviation = match.Success ? match.Groups[1].Value.ToUpperInvariant() : string.Empty;
        return match.Success;
    }
}
