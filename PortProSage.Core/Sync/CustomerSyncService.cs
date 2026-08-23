using Microsoft.Extensions.Logging;
using PortProSage.Core.Config;
using PortProSage.Core.Data;
using PortProSage.Core.Models;
using PortProSage.Core.PortPro;
using PortProSage.Core.Sage50;
using PortProSage.Core.Validation;

namespace PortProSage.Core.Sync;

public class CustomerSyncResult
{
    /// <summary>Customers whose PortPro updatedAt is newer than what's recorded
    /// in SyncStateRepository's customer_sync_state (i.e. genuinely changed since
    /// last synced) - the other ~200+ unchanged customers on the account aren't
    /// counted here at all, they're skipped before any Sage 50 lookup happens.
    /// For ExecuteSelectedRefreshAsync, this is simply how many of the operator's
    /// selected customers were actually considered (every selected one, since
    /// there's no changed-since-last-sync check for an explicit selection).</summary>
    public int Changed { get; set; }

    public int Updated { get; set; }

    /// <summary>Newly created in Sage 50 - only ExecuteSelectedRefreshAsync ever
    /// populates this (SyncChangedCustomersAsync never creates, only updates).</summary>
    public int Created { get; set; }

    /// <summary>Changed in PortPro, but not found in Sage 50 by name - only
    /// meaningful for SyncChangedCustomersAsync, which isn't this service's job to
    /// create them (that only happens when an actual invoice needs the customer,
    /// or the operator explicitly picks an INSERT candidate in the Customer
    /// Refresh grid - see ExecuteSelectedRefreshAsync). Still marked synced so the
    /// same "changed but not in Sage 50 yet" customer isn't re-checked on every
    /// single sweep.</summary>
    public int SkippedNotInSage50 { get; set; }

    public int Failed { get; set; }

    /// <summary>Per-customer detail - only ExecuteSelectedRefreshAsync populates
    /// this (one entry per customer it actually attempted); SyncChangedCustomersAsync
    /// leaves it empty. Lets the Admin app's Customer Refresh grid update each
    /// processed row in place (Applied/Date columns) instead of clearing the
    /// whole grid after a run - see SyncResult.CustomerRefreshOutcomes.</summary>
    public List<CustomerRefreshOutcome> Outcomes { get; } = new();
}

/// <summary>Three entry points:
/// - SyncChangedCustomersAsync: the periodic sweep (once per Automatic Service
///   cycle, once per Manual Run - see Worker.cs/Diagnostics.cs) that detects a
///   PortPro customer whose profile changed since this app last synced it, and
///   pushes just that change into the matching Sage 50 customer - see
///   Sage50Settings.SyncCustomerUpdatesFromPortPro for the on/off switch. Never
///   creates.
/// - ScanForRefreshAsync: the read-only PREVIEW half of the Admin app's Customer
///   Refresh tab (FilterType.CustomerRefreshScan) - builds the candidate list the
///   operator picks from.
/// - ExecuteSelectedRefreshAsync: the EXECUTE half (FilterType.
///   FullCustomerRefresh) - creates or updates exactly the customers the
///   operator selected from that preview.
///
/// Any Sage 50 write failure here terminates the whole process, same as
/// everywhere else in Sage50Client, EXCEPT CustomerProfileRejectedException
/// (Sage 50 rejecting one field's value, e.g. a too-long Email - confirmed live
/// 2026-08-23) which is recoverable and just skips that one customer.
///
/// Cheap by design: PortPro's customer LIST endpoint returns the exact same
/// full-profile shape as the single-customer fetch (confirmed live 2026-08-21),
/// and this account only has ~223 customers total, so one paginated fetch per
/// sweep is enough - no per-customer detail call, no date-filtered query needed
/// on PortPro's side.</summary>
public class CustomerSyncService
{
    private readonly PortProClient _portPro;
    private readonly ISage50Client _sage50;
    private readonly SyncStateRepository _state;
    private readonly Sage50Settings _settings;
    private readonly ILogger<CustomerSyncService> _logger;

    public CustomerSyncService(PortProClient portPro, ISage50Client sage50, SyncStateRepository state,
        Sage50Settings settings, ILogger<CustomerSyncService> logger)
    {
        _portPro = portPro;
        _sage50 = sage50;
        _state = state;
        _settings = settings;
        _logger = logger;
    }

    public async Task<CustomerSyncResult> SyncChangedCustomersAsync(CancellationToken ct)
    {
        var result = new CustomerSyncResult();

        if (!_settings.SyncCustomerUpdatesFromPortPro)
        {
            return result;
        }

        List<PortProCustomer> customers;
        try
        {
            customers = await _portPro.GetAllCustomersAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Customer sync: failed to fetch PortPro's customer list - skipping this sweep.");
            return result;
        }

        // Idempotent/cheap after the first real connect within this process's
        // lifetime (Sage50Client.ConnectAsync short-circuits once _connected is
        // true) - called here unconditionally rather than only when a change is
        // found, since SyncOrchestrator.RunAsync itself skips connecting entirely
        // when there's nothing due that cycle (see its UseWatermark early-return),
        // so Sage 50 isn't guaranteed to already be connected by the time this runs.
        await _sage50.ConnectAsync(ct);

        foreach (var customer in customers)
        {
            if (string.IsNullOrWhiteSpace(customer.CompanyName) || customer.UpdatedAt is null) continue;

            var lastSynced = _state.GetCustomerLastSyncedUpdatedAt(customer.Id);
            if (lastSynced is not null && customer.UpdatedAt <= lastSynced) continue; // unchanged since last sync

            result.Changed++;

            Sage50Customer? existing;
            try
            {
                existing = await _sage50.FindCustomerByNameAsync(customer.CompanyName, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Customer sync: failed to look up '{Customer}' in Sage 50 - skipping.", customer.CompanyName);
                result.Failed++;
                continue;
            }

            if (existing is null)
            {
                result.SkippedNotInSage50++;
                _state.MarkCustomerSynced(customer.Id, customer.CompanyName, customer.UpdatedAt.Value);
                continue;
            }

            var profile = InvoiceValidationService.BuildSage50Profile(customer.CompanyName, customer.Currency, customer);
            try
            {
                await _sage50.UpdateCustomerAsync(profile, ct);
            }
            catch (CustomerProfileRejectedException ex)
            {
                // Recoverable - nothing was written (see that exception's doc
                // comment). Confirmed live 2026-08-23: a too-long Email from a
                // multi-address PortPro billingEmail crashed two entire Full
                // Customer Refresh runs before this catch existed.
                _logger.LogWarning(ex, "Customer sync: Sage 50 rejected '{Customer}''s profile - skipping this customer, sweep continues.", customer.CompanyName);
                result.Failed++;
                continue;
            }
            result.Updated++;

            // Same DryRun trap as invoice imports (SyncOrchestrator's DRYRUN- guard
            // on MarkImported) - a dry-run UpdateCustomerAsync only logs, it never
            // actually writes, so marking this synced anyway would make the REAL
            // update silently never happen once DryRun is turned off (the change
            // would look "already synced" forever).
            if (!_settings.DryRun)
            {
                _state.MarkCustomerSynced(customer.Id, customer.CompanyName, customer.UpdatedAt.Value);
            }

            _logger.LogInformation("Customer sync: updated '{Customer}' from a changed PortPro profile.", customer.CompanyName);
        }

        if (result.Changed > 0)
        {
            _logger.LogInformation(
                "Customer sync: {Changed} customer(s) changed in PortPro since last sync - {Updated} updated in Sage 50, " +
                "{SkippedNotInSage50} not yet in Sage 50 (skipped), {Failed} failed.",
                result.Changed, result.Updated, result.SkippedNotInSage50, result.Failed);
        }

        return result;
    }

    /// <summary>Read-only preview for the Admin app's Customer Refresh grid - see
    /// FilterType.CustomerRefreshScan's doc comment. No Sage 50 writes at all;
    /// only LoadByName lookups and field reads.</summary>
    public async Task<List<CustomerRefreshCandidate>> ScanForRefreshAsync(CancellationToken ct)
    {
        var customers = await _portPro.GetAllCustomersAsync(ct);
        await _sage50.ConnectAsync(ct);

        // Loaded once (not one query per customer) - see GetAllCustomerRefreshOutcomes's
        // doc comment. Lets every candidate show its last-known Run Selected
        // outcome immediately, even before anything is (re-)selected this session.
        var lastOutcomes = _state.GetAllCustomerRefreshOutcomes();

        var candidates = new List<CustomerRefreshCandidate>();
        foreach (var customer in customers)
        {
            if (string.IsNullOrWhiteSpace(customer.CompanyName)) continue;

            var portProProfile = InvoiceValidationService.BuildSage50Profile(customer.CompanyName, customer.Currency, customer);

            Sage50Customer? existing;
            try
            {
                existing = await _sage50.FindCustomerByNameAsync(customer.CompanyName, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Customer refresh scan: failed to look up '{Customer}' in Sage 50 - excluded from the list.", customer.CompanyName);
                continue;
            }

            string? sageDetails = null;
            if (existing is not null)
            {
                var sageProfile = await _sage50.GetCustomerProfileByNameAsync(customer.CompanyName, ct);
                sageDetails = sageProfile is not null ? Sage50ProfileFormatter.Describe(sageProfile) : null;
            }
            else
            {
                // Quoted and length-tagged deliberately - confirmed live 2026-08-24
                // that a customer visibly present in Sage 50 can still show as
                // INSERT here, and the cause (a hidden trailing space, a case
                // difference, or some other byte-level mismatch CustomerLedger.
                // LoadByName's undocumented exact-match behavior is sensitive to)
                // is invisible in a plain, unquoted log line - this makes it
                // immediately checkable without re-fetching the raw API value by
                // hand every time.
                _logger.LogInformation(
                    "Customer refresh scan: '{Customer}' not found in Sage 50 by exact name match - searched for " +
                    "\"{SearchedName}\" (length={Length}). If this customer visibly exists in Sage 50, check its " +
                    "Name field there for a hidden trailing space or a case difference from this exact string.",
                    customer.CompanyName, customer.CompanyName, customer.CompanyName.Length);
            }

            var hasLastOutcome = lastOutcomes.TryGetValue(customer.Id, out var lastOutcome);

            candidates.Add(new CustomerRefreshCandidate
            {
                PortProCustomerId = customer.Id,
                CompanyName = customer.CompanyName,
                Operation = existing is null ? "INSERT" : "UPDATE",
                PortProDetails = DescribeWithTerms(portProProfile, customer.DefaultPaymentTerms),
                SageCustomerName = existing?.Name,
                SageDetails = sageDetails,
                LastOperationSuccess = hasLastOutcome ? lastOutcome.Success : null,
                LastAppliedAtUtc = hasLastOutcome ? lastOutcome.AppliedAtUtc : null
            });
        }

        _logger.LogInformation(
            "Customer refresh scan: {Total} PortPro customer(s) considered - {Insert} to insert, {Update} to update.",
            candidates.Count, candidates.Count(c => c.Operation == "INSERT"), candidates.Count(c => c.Operation == "UPDATE"));

        return candidates.OrderBy(c => c.CompanyName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Appends PortPro's customer-level default payment terms (informational
    /// only - confirmed via live SDK reflection that Sage 50 has no per-customer
    /// terms property to write this to at all; only the invoice-posting object
    /// does, via SetTermDiscNetDay - see Sage50Invoice.NetTermDays) so the operator
    /// can see it in the grid even though it's never actually applied to Sage 50
    /// by this tab.</summary>
    private static string DescribeWithTerms(Sage50CustomerProfile profile, PortProCustomerPaymentTerms? terms)
    {
        var described = Sage50ProfileFormatter.Describe(profile);
        if (terms?.Days is null) return described;

        var method = string.IsNullOrWhiteSpace(terms.PaymentTermsMethod) ? "days" : terms.PaymentTermsMethod;
        return $"{described}, terms={terms.Days} {method} from invoice date (reference only - not written to Sage 50)";
    }

    /// <summary>Processes EXACTLY the PortPro customer ids the operator selected in
    /// the Admin app's Customer Refresh grid (built from a prior
    /// ScanForRefreshAsync) - never "all customers" implicitly. Creates a customer
    /// in Sage 50 if it doesn't already exist (INSERT - gated by Sage50Settings.
    /// AutoCreateCustomers, the same global policy switch that gates auto-create-
    /// from-invoice), or pushes its changed profile if it does (UPDATE).
    /// Deliberately NOT gated by SyncCustomerUpdatesFromPortPro - that setting only
    /// controls the automatic incidental sweep; this is an explicit, operator-
    /// selected action with its own confirmation dialog in the Admin app.</summary>
    public async Task<CustomerSyncResult> ExecuteSelectedRefreshAsync(IReadOnlyCollection<string> selectedPortProIds, CancellationToken ct)
    {
        var result = new CustomerSyncResult();
        if (selectedPortProIds.Count == 0) return result;

        var selectedIds = new HashSet<string>(selectedPortProIds, StringComparer.OrdinalIgnoreCase);
        var customers = await _portPro.GetAllCustomersAsync(ct);
        await _sage50.ConnectAsync(ct);

        foreach (var customer in customers)
        {
            if (!selectedIds.Contains(customer.Id) || string.IsNullOrWhiteSpace(customer.CompanyName)) continue;

            result.Changed++;
            var appliedAt = DateTimeOffset.UtcNow;
            var syncedAt = customer.UpdatedAt ?? DateTimeOffset.UtcNow;
            var profile = InvoiceValidationService.BuildSage50Profile(customer.CompanyName, customer.Currency, customer);

            void RecordOutcome(string operation, bool success, string message)
            {
                result.Outcomes.Add(new CustomerRefreshOutcome
                {
                    PortProCustomerId = customer.Id,
                    CompanyName = customer.CompanyName,
                    Operation = operation,
                    Success = success,
                    Message = message,
                    AppliedAtUtc = appliedAt
                });

                // NOT persisted for a Dry Run - confirmed 2026-08-24 that a
                // simulated attempt must never be remembered as if it were a
                // real one; nothing actually happened in Sage 50, so recording a
                // status/date for it would misleadingly survive into a later
                // session's Extract as if this customer had genuinely been
                // created/updated. Persisted for every REAL attempt regardless
                // of success/failure though - see RecordCustomerRefreshOutcome's
                // doc comment for why this is always the true most recent real
                // attempt, overwritten every time this customer is selected and
                // run again for real.
                if (!_settings.DryRun)
                {
                    _state.RecordCustomerRefreshOutcome(customer.Id, customer.CompanyName, operation, success, message, appliedAt);
                }
            }

            Sage50Customer? existing;
            try
            {
                existing = await _sage50.FindCustomerByNameAsync(customer.CompanyName, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Customer refresh: failed to look up '{Customer}' in Sage 50 - skipping.", customer.CompanyName);
                result.Failed++;
                RecordOutcome("INSERT", false, $"Lookup failed: {ex.Message}");
                continue;
            }

            if (existing is null)
            {
                try
                {
                    await _sage50.CreateCustomerAsync(profile, _settings.DefaultReceivableAccount, ct);
                    result.Created++;
                    if (!_settings.DryRun) _state.MarkCustomerSynced(customer.Id, customer.CompanyName, syncedAt);
                    _logger.LogInformation("Customer refresh: created new Sage 50 customer '{Customer}'.", customer.CompanyName);
                    RecordOutcome("INSERT", true, _settings.DryRun ? "DRY RUN - would create." : "Created.");
                }
                catch (CustomerProfileRejectedException ex)
                {
                    _logger.LogWarning(ex, "Customer refresh: Sage 50 rejected '{Customer}''s profile on create - skipping.", customer.CompanyName);
                    result.Failed++;
                    RecordOutcome("INSERT", false, ex.Message);
                }
                catch (InvalidOperationException ex)
                {
                    // e.g. Sage50Settings.AutoCreateCustomers is off - a known,
                    // non-fatal configuration state, not evidence of a compromised
                    // session (this throw happens before Sage50Client's own
                    // try/catch, so it wouldn't otherwise be caught here).
                    _logger.LogWarning(ex, "Customer refresh: could not create '{Customer}' - skipping.", customer.CompanyName);
                    result.Failed++;
                    RecordOutcome("INSERT", false, ex.Message);
                }
                continue;
            }

            try
            {
                await _sage50.UpdateCustomerAsync(profile, ct);
                result.Updated++;
                if (!_settings.DryRun) _state.MarkCustomerSynced(customer.Id, customer.CompanyName, syncedAt);
                _logger.LogInformation("Customer refresh: updated '{Customer}'.", customer.CompanyName);
                RecordOutcome("UPDATE", true, _settings.DryRun ? "DRY RUN - would update." : "Updated.");
            }
            catch (CustomerProfileRejectedException ex)
            {
                _logger.LogWarning(ex, "Customer refresh: Sage 50 rejected '{Customer}''s profile - skipping.", customer.CompanyName);
                result.Failed++;
                RecordOutcome("UPDATE", false, ex.Message);
            }
        }

        _logger.LogInformation(
            "Customer refresh: {Selected} customer(s) selected - {Created} created, {Updated} updated, {Failed} failed.",
            result.Changed, result.Created, result.Updated, result.Failed);

        return result;
    }
}
