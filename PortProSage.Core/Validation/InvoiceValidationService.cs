using Microsoft.Extensions.Logging;
using PortProSage.Core.Config;
using PortProSage.Core.Data;
using PortProSage.Core.Models;
using PortProSage.Core.PortPro;
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
    private readonly PortProClient _portPro;
    private readonly SyncStateRepository _state;
    private readonly Sage50Settings _settings;
    private readonly ILogger<InvoiceValidationService> _logger;

    public InvoiceValidationService(ISage50Client sage50, PortProClient portPro, SyncStateRepository state,
        Sage50Settings settings, ILogger<InvoiceValidationService> logger)
    {
        _sage50 = sage50;
        _portPro = portPro;
        _state = state;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>Once a customer is found-or-created for this run, every later
    /// invoice for the same company name reuses that answer instead of repeating
    /// the Sage 50 CustomerLedger.LoadByName lookup (and, for auto-create, the
    /// PortPro profile fetch) all over again - a batch of many invoices for the
    /// same customer used to re-check the same answer once per invoice for no
    /// reason. Safe even if the customer is deleted from Sage 50 mid-run: this
    /// only skips the redundant PRE-check - Sage50Client.CreateInvoiceAsync's own
    /// SelectAPARLedger call independently re-validates the customer right before
    /// posting and throws a clean, per-invoice failure if it no longer resolves,
    /// so a stale cache entry can never cause a silent wrong write, just a normal
    /// "IMPORT ERROR" on that one invoice.
    ///
    /// This class is a DI singleton (reused for every poll cycle of a long-running
    /// Automatic Service, not recreated per run), so the cache MUST be cleared at
    /// the start of every run - see ResetPerRunCache, called once at the top of
    /// SyncOrchestrator.RunAsync - or a customer renamed/deleted between two
    /// separate runs would keep resolving to a run-old answer indefinitely.</summary>
    private readonly Dictionary<string, string> _resolvedCustomerCodeByNameThisRun = new(StringComparer.OrdinalIgnoreCase);

    public void ResetPerRunCache() => _resolvedCustomerCodeByNameThisRun.Clear();

    /// <summary>Evicts one customer from the per-run cache - called by
    /// SyncOrchestrator when Sage50Client.CreateInvoiceAsync throws
    /// CustomerNotFoundException, so a cached-but-now-stale "this customer
    /// exists" answer doesn't keep being trusted for the rest of THIS run too.
    /// The very next invoice for the same customer (in this same run, if there
    /// is one) gets a fresh Sage 50 lookup - and, since the customer is now
    /// genuinely missing, a real chance to auto-recreate it and succeed
    /// immediately, not just via a later gap-fill/Continue run.</summary>
    public void InvalidateCustomer(string name)
    {
        if (!string.IsNullOrWhiteSpace(name)) _resolvedCustomerCodeByNameThisRun.Remove(name);
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

        if (_resolvedCustomerCodeByNameThisRun.TryGetValue(customerName, out var cachedCode))
        {
            result.ResolvedSage50CustomerCode = cachedCode;
            return;
        }

        var existing = await _sage50.FindCustomerByNameAsync(customerName, ct);
        if (existing is not null)
        {
            result.ResolvedSage50CustomerCode = existing.Code;
            _resolvedCustomerCodeByNameThisRun[customerName] = existing.Code;
            return;
        }

        if (!_settings.AutoCreateCustomers)
        {
            result.Errors.Add($"Customer '{customerName}' not found in Sage 50 and auto-create is disabled.");
            return;
        }

        try
        {
            // Pull PortPro's full customer profile (address/email/contact/currency -
            // see PortProCustomer's doc comment) before creating, rather than just
            // the bare name the invoice's own lightweight "caller" object carries -
            // confirmed live 2026-08-21 this is a genuinely separate, richer PortPro
            // object at GET /customer/{id}. Falls back to name + invoice-level
            // currency only if the caller has no id, or the fetch itself fails -
            // never lets a profile-fetch problem block the customer/invoice from
            // being created at all.
            PortProCustomer? fullProfile = null;
            if (!string.IsNullOrWhiteSpace(invoice.Caller?.Id))
            {
                try
                {
                    fullProfile = await _portPro.GetCustomerAsync(invoice.Caller.Id, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not fetch PortPro's full customer profile for '{Customer}' (id {Id}) - creating with name/currency only.",
                        customerName, invoice.Caller.Id);
                }
            }

            var profile = BuildSage50Profile(customerName, invoice.Caller?.Currency, fullProfile);
            var created = await _sage50.CreateCustomerAsync(profile, _settings.DefaultReceivableAccount, ct);
            result.ResolvedSage50CustomerCode = created.Code;
            _resolvedCustomerCodeByNameThisRun[customerName] = created.Code;
            result.Warnings.Add($"Customer '{customerName}' did not exist in Sage 50 and was auto-created (code {created.Code}).");

            if (fullProfile is not null)
            {
                _state.MarkCustomerSynced(fullProfile.Id, customerName, fullProfile.UpdatedAt ?? DateTimeOffset.UtcNow);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to auto-create Sage 50 customer '{Customer}'", customerName);
            result.Errors.Add($"Failed to auto-create customer '{customerName}': {ex.Message}");
        }
    }

    /// <summary>Maps PortPro's full customer profile onto Sage50CustomerProfile -
    /// shared logic, also used by CustomerSyncService for the update path, so a
    /// customer's mapped fields are identical whether it just got auto-created or
    /// is being refreshed later. fullProfile null means only name/currency are
    /// available (see ValidateCustomerAsync's fallback above).</summary>
    public static Sage50CustomerProfile BuildSage50Profile(string name, string? fallbackCurrency, PortProCustomer? fullProfile)
    {
        if (fullProfile is null)
        {
            return new Sage50CustomerProfile { Name = name, CurrencyCode = fallbackCurrency };
        }

        return new Sage50CustomerProfile
        {
            Name = name,
            CurrencyCode = string.IsNullOrWhiteSpace(fullProfile.Currency) ? fallbackCurrency : fullProfile.Currency,
            Contact = fullProfile.MainContactName,
            Street1 = fullProfile.Address1,
            City = fullProfile.City,
            Province = fullProfile.State,
            Country = fullProfile.Country,
            PostalCode = fullProfile.ZipCode,
            Phone1 = fullProfile.Mobile,
            // BillingEmail (real, human-entered) - never the bare "email" field,
            // which is a PortPro-generated proxy address. See PortProCustomer's
            // doc comment.
            Email = FirstEmail(fullProfile.BillingEmail)
        };
    }

    /// <summary>PortPro's billingEmail is sometimes a comma-joined list of multiple
    /// addresses (confirmed live 2026-08-23 for 'MANITOULIN GLOBAL FORWARDING
    /// TORONTO': "MGFPayables@MGFGroup.com,oceanimptor@mgfgroup.com,MGFCENTRAL@
    /// mgfgroup.com," - 74 characters) - Sage 50's Email field can only hold one
    /// address and has its own hard 50-character limit (confirmed via a real
    /// SimplySDK.InvalidEntryException that crashed two Full Customer Refresh runs
    /// before Sage50Client.CustomerProfileRejectedException existed to catch it
    /// gracefully instead). Takes just the first address, which is virtually
    /// always well under that limit on its own - a single email losing its
    /// secondary CC addresses in Sage 50 is a far smaller loss than the whole
    /// customer failing to sync at all.</summary>
    private static string? FirstEmail(string? billingEmail)
    {
        if (string.IsNullOrWhiteSpace(billingEmail)) return billingEmail;
        var first = billingEmail.Split(',')[0].Trim();
        return first.Length == 0 ? null : first;
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
                else if (!TryResolveAccountForCharge(invoice.ReferenceNumber, line, result, out revenueAccount, out var resolveError))
                {
                    result.Errors.Add(resolveError!);
                    continue;
                }

                result.ResolvedItemCodesByChargeName[line.Name] = existingItem.Code;
            }
            else if (_settings.AutoCreateItems)
            {
                if (!TryResolveAccountForCharge(invoice.ReferenceNumber, line, result, out revenueAccount, out var resolveError))
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
                // "Ignore account 1-on-1 match and apply default" (Sage 50 tab) - only
                // kicks in when the account that failed isn't already the default (no
                // further fallback exists past that), and only falls back once: if the
                // default ALSO can't be confirmed, this is still a hard error either way.
                if (_settings.IgnoreAccountMismatchUseDefault
                    && !string.IsNullOrWhiteSpace(_settings.DefaultRevenueAccount)
                    && !string.Equals(revenueAccount, _settings.DefaultRevenueAccount, StringComparison.OrdinalIgnoreCase))
                {
                    result.Warnings.Add(
                        $"Charge '{line.Name}' was mapped to account '{revenueAccount}', which could not be " +
                        $"confirmed in Sage 50 - used the Default revenue account '{_settings.DefaultRevenueAccount}' " +
                        "instead (\"Ignore account 1-on-1 match and apply default\" is checked).");
                    revenueAccount = _settings.DefaultRevenueAccount;

                    if (!await _sage50.AccountExistsAsync(revenueAccount, ct))
                    {
                        result.Errors.Add(
                            $"Charge '{line.Name}': neither the mapped account nor the Default revenue account " +
                            $"'{revenueAccount}' could be confirmed in Sage 50's chart of accounts.");
                        continue;
                    }
                }
                else
                {
                    result.Errors.Add($"Revenue account '{revenueAccount}' for charge '{line.Name}' does not exist in Sage 50's chart of accounts.");
                    continue;
                }
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
    private bool TryResolveAccountForCharge(string invoiceRef, PortProPricingLine line, ValidationResult result, out string account, out string? error)
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

        // Flagged as a WARNING (not just a silent fallback) whenever a charge falls
        // through to the default account, whether that's because it has no
        // ChargeAccountMap entry at all or because its existing Sage 50 item carried
        // an invalid account (see the PREPULL/STORAGE/YARD STORAGE - LOADED incident
        // above) - visible per-invoice so a mapping gap is caught immediately rather
        // than discovered later. Logged via _logger.LogWarning (not just added to
        // result.Warnings) so it also lands in the searchable log file and shows up
        // in the Admin app's Warnings/Validation tab, which filters on "[WRN]".
        var warning = $"Revenue account '{line.GlCode ?? "(none)"}' in PortPro for charge '{line.Name}' was mapped " +
                      $"to DEFAULT account '{account}' in Sage50.";
        result.Warnings.Add(warning);
        _logger.LogWarning("Invoice {Ref}: {Message}", invoiceRef, warning);
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
