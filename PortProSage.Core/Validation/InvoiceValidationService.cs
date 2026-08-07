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

            // Look up by the same derived code CreateServiceItemAsync would create it
            // under (see MakeItemCode) - looking up by the raw PortPro charge name
            // instead (as this used to) never matches an already-created item, since
            // its actual Sage 50 code is the sanitized/truncated/prefixed form, not
            // the raw name. That mismatch meant every previously-created item was
            // "not found" on every later run, triggering a duplicate-code create
            // attempt that Sage 50 rejects with an opaque SDK error - confirmed live
            // 2026-08-04 against the real PICKUPDELIVE item.
            var itemCode = MakeItemCode(line.Name);
            var existingItem = await _sage50.FindItemByCodeOrDescriptionAsync(itemCode, ct);
            string revenueAccount;

            if (existingItem is not null)
            {
                // Trust the account already configured on the existing Sage 50 item ONLY
                // if that account actually exists - it may have been set up deliberately
                // (e.g. by hand in Sage 50), and re-deriving it from ChargeAccountMap/
                // default here would silently discard that. But confirmed live 2026-08-07
                // this blind trust was itself a bug: PREPULL/STORAGE/YARD STORAGE - LOADED
                // were auto-created back when the (now-fixed) glCode-fallback bug was still
                // live, so their EXISTING Sage 50 item record permanently carries the
                // invalid glCode "4020" as its revenue account - every later run just kept
                // trusting that stale, invalid value and failing validation forever, never
                // reaching the ChargeAccountMap/Default fallback below. An existing item
                // with a missing or invalid account is treated the same as no item at all -
                // same priority as everywhere else: ChargeAccountMap > Default > Error.
                var existingAccountValid = !string.IsNullOrWhiteSpace(existingItem.RevenueAccount)
                    && await _sage50.AccountExistsAsync(existingItem.RevenueAccount, ct);

                if (existingAccountValid)
                {
                    revenueAccount = existingItem.RevenueAccount!;
                }
                else if (!TryResolveAccountForCharge(line, out revenueAccount, out var resolveError))
                {
                    result.Errors.Add(resolveError!);
                    continue;
                }

                result.ResolvedItemCodesByChargeName[line.Name] = existingItem.Code;
            }
            else if (_settings.AutoCreateItems)
            {
                if (!TryResolveAccountForCharge(line, out revenueAccount, out var resolveError))
                {
                    result.Errors.Add(resolveError!);
                    continue;
                }

                try
                {
                    var created = await _sage50.CreateServiceItemAsync(itemCode, line.Name, revenueAccount, ct);
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

            if (!await _sage50.AccountExistsAsync(revenueAccount, ct))
            {
                result.Errors.Add($"Revenue account '{revenueAccount}' for charge '{line.Name}' does not exist in Sage 50's chart of accounts.");
                continue;
            }

            result.ResolvedRevenueAccountByChargeName[line.Name] = revenueAccount;
        }
    }

    /// <summary>
    /// Resolution order: Sage50Settings.ChargeAccountMap's Sage50AccountNumber for
    /// this charge name (if a row exists and it's non-blank) > DefaultRevenueAccount.
    /// PortPro's own glCode is NOT consulted - confirmed live 2026-08-05 this was a
    /// real bug: PREPULL/STORAGE/YARD STORAGE - LOADED carry PortPro glCode "4020",
    /// which doesn't exist in this company's chart of accounts, so using it instead
    /// of falling through to the default caused every one of those charges to fail
    /// validation. The actual rule is simpler and was stated explicitly: unmapped
    /// charges always fall back to DefaultRevenueAccount, full stop.
    ///
    /// Returns false (with an error message, no account) if neither resolves to
    /// anything, i.e. no ChargeAccountMap entry and DefaultRevenueAccount is blank -
    /// per the same rule, that's the one case that must stop the process rather
    /// than silently post to an undefined account.
    /// </summary>
    private bool TryResolveAccountForCharge(PortProPricingLine line, out string account, out string? error)
    {
        error = null;

        var mapping = _settings.ChargeAccountMap.FirstOrDefault(
            m => string.Equals(m.PortProChargeName, line.Name, StringComparison.OrdinalIgnoreCase));

        if (mapping is not null && !string.IsNullOrWhiteSpace(mapping.Sage50AccountNumber))
        {
            account = mapping.Sage50AccountNumber;
            return true;
        }

        if (string.IsNullOrWhiteSpace(_settings.DefaultRevenueAccount))
        {
            account = string.Empty;
            error = $"Charge '{line.Name}' has no ChargeAccountMap entry, and " +
                    "Sage50Settings.DefaultRevenueAccount is not configured - cannot resolve a GL account to post to.";
            return false;
        }

        account = _settings.DefaultRevenueAccount;
        return true;
    }

    /// <summary>
    /// "PP_" prefixed so an auto-created item's code can never collide with
    /// anything created before this scheme existed (or anything a client might
    /// enter by hand under the plain charge name) - confirmed live 2026-08-04 that
    /// a bare derived code (no prefix) can collide with a legacy/dangling Sage 50
    /// item code and cause a duplicate-code create failure. Total length kept at
    /// the same conservative 12 characters as before, prefix included.
    /// </summary>
    private static string MakeItemCode(string chargeName)
    {
        const string prefix = "PP_";
        var cleaned = new string(chargeName.Where(char.IsLetterOrDigit).ToArray());
        var maxBodyLength = 12 - prefix.Length;
        var body = cleaned.Length <= maxBodyLength ? cleaned : cleaned.Substring(0, maxBodyLength);
        return prefix + body.ToUpperInvariant();
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
    internal static bool TryGetTaxAbbreviation(string chargeName, out string abbreviation)
    {
        var match = TaxChargeNamePattern.Match(chargeName);
        abbreviation = match.Success ? match.Groups[1].Value.ToUpperInvariant() : string.Empty;
        return match.Success;
    }
}
