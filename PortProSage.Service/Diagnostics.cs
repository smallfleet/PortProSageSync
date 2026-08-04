using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PortProSage.Core.Config;
using PortProSage.Core.Data;
using PortProSage.Core.Models;
using PortProSage.Core.PortPro;
using PortProSage.Core.Sage50;
using PortProSage.Core.Sync;

namespace PortProSage.Service;

/// <summary>
/// Standalone connectivity checks, invoked via command-line flag (see Program.cs)
/// so each half of the integration can be validated on its own before wiring them
/// together - e.g. confirm PortPro auth/fetch works even before the Sage 50 SDK
/// is installed, or confirm the Sage 50 SDK connects even with no PortPro
/// credentials configured yet.
/// </summary>
public static class Diagnostics
{
    public static async Task<int> RunAsync(string mode, IServiceProvider services, ILogger logger, CancellationToken ct)
    {
        switch (mode.ToLowerInvariant())
        {
            case "portpro":
                return await CheckPortProAsync(services, logger, ct);

            case "sage50":
                return await CheckSage50Async(services, logger, ct);

            default:
                logger.LogError("Unknown --diagnose mode '{Mode}'. Expected 'portpro' or 'sage50'.", mode);
                return 1;
        }
    }

    private static async Task<int> CheckPortProAsync(IServiceProvider services, ILogger logger, CancellationToken ct)
    {
        logger.LogInformation("=== STAGE 1: PortPro connectivity check (Sage 50 not touched) ===");
        try
        {
            var client = services.GetRequiredService<PortProClient>();

            // Small, cheap probe: last 24 hours of changed invoices, just to prove
            // auth + the invoices endpoint work end to end.
            var request = new SyncRequest
            {
                FilterType = FilterType.LastChangedDate,
                From = DateTimeOffset.UtcNow.AddDays(-1),
                To = DateTimeOffset.UtcNow,
                RequestedBy = "diagnostic"
            };

            var invoices = await client.GetInvoicesAsync(request, ct);
            logger.LogInformation(
                "SUCCESS: authenticated with PortPro and fetched {Count} invoice(s) changed in the last 24 hours.",
                invoices.Count);

            foreach (var inv in invoices.Take(5))
            {
                logger.LogInformation(
                    "  sample: ref={Ref}, customer={Customer}, total={Total}, updatedAt={Updated}",
                    inv.ReferenceNumber, inv.Caller?.CompanyName ?? inv.CallerName, inv.TotalAmount, inv.UpdatedAt);
            }

            return 0;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "FAILED: could not fetch invoices from PortPro. Check PortPro:AccessToken/RefreshToken " +
                                 "and BaseUrl in appsettings (get these from PortPro's own integration/API settings screen), " +
                                 "and double-check the query parameter names noted in PortProClient.BuildQueryString " +
                                 "against your actual PortPro API reference.");
            return 1;
        }
    }

    private static async Task<int> CheckSage50Async(IServiceProvider services, ILogger logger, CancellationToken ct)
    {
        logger.LogInformation("=== STAGE 2: Sage 50 SDK connectivity check (PortPro not touched) ===");
        try
        {
            var sage50 = services.GetRequiredService<ISage50Client>();
            await sage50.ConnectAsync(ct);
            logger.LogInformation("SUCCESS: connected to Sage 50 and opened the configured company file.");

            // A harmless read-only call to confirm the object model actually works,
            // not just that COM instantiation succeeded.
            var probe = await sage50.FindCustomerByNameAsync("__PortProSage_Diagnostic_Probe__", ct);
            logger.LogInformation(
                "Read-only customer lookup completed without error (found: {Found}) - confirms the Customers collection is reachable.",
                probe is not null);

            return 0;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "FAILED: could not connect to Sage 50. Check Sage50:CompanyDataPath/UserName/Password/" +
                                 "AppId/AppName in appsettings, confirm lib/Sage50SDK is populated with SDK assemblies " +
                                 "matching this machine's installed Sage 50 version (see the startup log line for the " +
                                 "bundled SDK version), and confirm no other exclusive-mode session is blocking access " +
                                 "to the company file.");
            return 1;
        }
    }

    /// <summary>
    /// Manually corrects the persisted "continue from where we left off" anchor to a
    /// specific PortPro invoice - both the date watermark (which actually drives the
    /// next watermark-driven fetch's From) and the last-processed-invoice-number
    /// (display/audit). Never touches Sage 50 - looks the invoice up read-only via
    /// PortProClient (the same already-verified GetInvoicesAsync/InvoiceNumberRange
    /// path used for real range triggers) and writes directly to SyncStateRepository.
    /// </summary>
    public static async Task<int> SetAnchorAsync(string referenceNumber, IServiceProvider services, ILogger logger, CancellationToken ct)
    {
        logger.LogInformation("=== SET ANCHOR: correcting the persisted watermark to invoice '{Ref}' (PortPro read-only, Sage 50 not touched) ===", referenceNumber);
        try
        {
            var client = services.GetRequiredService<PortProClient>();
            var state = services.GetRequiredService<SyncStateRepository>();

            var lookup = new SyncRequest
            {
                FilterType = FilterType.InvoiceNumberRange,
                StartInvoiceNumber = referenceNumber,
                EndInvoiceNumber = referenceNumber,
                RequestedBy = "set-anchor"
            };

            var matches = await client.GetInvoicesAsync(lookup, ct);
            var invoice = matches.FirstOrDefault();

            if (invoice is null)
            {
                logger.LogError("FAILED: no PortPro invoice found with reference number '{Ref}' - anchor left unchanged.", referenceNumber);
                return 1;
            }

            if (invoice.UpdatedAt is null)
            {
                logger.LogError("FAILED: invoice '{Ref}' was found but has no updatedAt - cannot set the date watermark from it. Anchor left unchanged.", referenceNumber);
                return 1;
            }

            state.SetLastChangedWatermark(invoice.UpdatedAt.Value);
            state.SetLastProcessedInvoiceNumber(invoice.ReferenceNumber);

            logger.LogInformation(
                "SUCCESS: anchor set to invoice {Ref} (updatedAt={UpdatedAt}). The next watermark-driven run (automatic " +
                "poll or a no-mode manual trigger) will fetch from this point onward.",
                invoice.ReferenceNumber, invoice.UpdatedAt);

            return 0;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "FAILED: could not set anchor to invoice '{Ref}'.", referenceNumber);
            return 1;
        }
    }

    /// <summary>
    /// Real (non-DryRun) write of up to `count` invoices strictly after
    /// `afterInvoiceNumber`, ordered ascending, amount > 0 only. Forces DryRun
    /// off in-memory for THIS PROCESS ONLY (never touches appsettings.Local.json
    /// or any environment variable) - this command never starts the Worker's
    /// automatic-poll loop, so there is no automatic run for that override to
    /// leak into; the process runs this one bounded batch and exits. The run
    /// uses an explicit invoice-number range (UseWatermark=false), so it never
    /// touches the persisted watermark/last-processed-invoice-number - call
    /// --set-anchor afterward to advance it to whatever was actually transferred.
    /// </summary>
    public static async Task<int> RealTransferAsync(string afterInvoiceNumber, int count, IServiceProvider services, ILogger logger, CancellationToken ct)
    {
        logger.LogWarning(
            "=== REAL TRANSFER: about to WRITE up to {Count} real invoice(s) to Sage 50, strictly after '{After}' ===",
            count, afterInvoiceNumber);

        var sage50Settings = services.GetRequiredService<Sage50Settings>();
        if (sage50Settings.DryRun)
        {
            sage50Settings.DryRun = false;
            logger.LogWarning("DryRun forced to false in-memory for this process only - appsettings.Local.json and environment variables are untouched.");
        }

        var portPro = services.GetRequiredService<PortProClient>();
        var orchestrator = services.GetRequiredService<SyncOrchestrator>();

        try
        {
            // No server-side reference-number filter (see PortProClient.GetInvoicesAsync) -
            // this pages through every invoice from afterInvoiceNumber's neighborhood
            // onward and filters/sorts client-side to find exactly which ones qualify.
            var discovery = new SyncRequest
            {
                FilterType = FilterType.InvoiceNumberRange,
                StartInvoiceNumber = afterInvoiceNumber,
                RequestedBy = "real-transfer-discovery"
            };

            var fromAnchor = await portPro.GetInvoicesAsync(discovery, ct);
            var candidates = fromAnchor
                .Where(i => string.CompareOrdinal(i.ReferenceNumber, afterInvoiceNumber) > 0 && i.TotalAmount > 0m)
                .OrderBy(i => i.ReferenceNumber, StringComparer.Ordinal)
                .Take(count)
                .ToList();

            if (candidates.Count == 0)
            {
                logger.LogError("FAILED: no eligible invoices (amount > 0) found strictly after '{After}'. Nothing written.", afterInvoiceNumber);
                return 1;
            }

            if (candidates.Count < count)
            {
                logger.LogWarning(
                    "Only {Found} eligible invoice(s) found after '{After}' (requested {Count}) - proceeding with what's available.",
                    candidates.Count, afterInvoiceNumber, count);
            }

            var start = candidates[0].ReferenceNumber;
            var end = candidates[candidates.Count - 1].ReferenceNumber;
            logger.LogWarning("REAL TRANSFER selecting {Selected} invoice(s), range {Start}..{End}.", candidates.Count, start, end);

            var request = new SyncRequest
            {
                FilterType = FilterType.InvoiceNumberRange,
                StartInvoiceNumber = start,
                EndInvoiceNumber = end,
                UseWatermark = false,
                RequestedBy = "real-transfer"
            };

            var result = await orchestrator.RunAsync(request, ct);

            logger.LogWarning(
                "REAL TRANSFER complete: fetched={Fetched} imported={Imported} alreadyImported={AlreadyImported} " +
                "zeroAmount={ZeroAmount} failedValidation={FailedVal} failedImport={FailedImp}",
                result.InvoicesFetched, result.InvoicesImported, result.InvoicesSkippedAlreadyImported,
                result.InvoicesSkippedZeroOrNegativeAmount, result.InvoicesFailedValidation, result.InvoicesFailedImport);

            foreach (var outcome in result.Outcomes)
            {
                logger.LogInformation(
                    "  {Ref}: success={Success} sage50Number={SageNo} messages=[{Messages}]",
                    outcome.ReferenceNumber, outcome.Success, outcome.Sage50InvoiceNumber ?? "(none)", string.Join(" | ", outcome.Messages));
            }

            return result.InvoicesFailedImport > 0 || result.InvoicesFailedValidation > 0 ? 1 : 0;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "FAILED: real transfer errored outside the per-invoice loop.");
            return 1;
        }
    }

    /// <summary>
    /// Isolated diagnostic: create exactly one throwaway service item (real write,
    /// not DryRun - forced off in-memory for this process only) under a caller-
    /// supplied, never-before-used code, to distinguish "this specific revenue
    /// account can't be assigned via the SDK" from "this specific item code can't
    /// be (re)used" as the cause of a Save() failure. Safe to delete afterward in
    /// Sage 50 directly.
    /// </summary>
    public static async Task<int> CreateTestItemAsync(string code, string revenueAccount, IServiceProvider services, ILogger logger, CancellationToken ct)
    {
        logger.LogWarning("=== CREATE TEST ITEM: real write of one throwaway service item, code='{Code}', account='{Account}' ===", code, revenueAccount);

        var sage50Settings = services.GetRequiredService<Sage50Settings>();
        if (sage50Settings.DryRun)
        {
            sage50Settings.DryRun = false;
            logger.LogWarning("DryRun forced to false in-memory for this process only.");
        }

        var sage50 = services.GetRequiredService<ISage50Client>();
        try
        {
            await sage50.ConnectAsync(ct);
            var existing = await sage50.FindItemByCodeOrDescriptionAsync(code, ct);
            if (existing is not null)
            {
                logger.LogError("FAILED: code '{Code}' already exists in Sage 50 (revenueAccount={Account}) - pick a different code.", code, existing.RevenueAccount);
                return 1;
            }

            var created = await sage50.CreateServiceItemAsync(code, $"PortProSage diagnostic test item {code} - safe to delete", revenueAccount, ct);
            logger.LogInformation("SUCCESS: created test item code={Code}, revenueAccount={Account}. Delete it in Sage 50 when done.", created.Code, created.RevenueAccount);
            return 0;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "FAILED: could not create test item '{Code}'.", code);
            return 1;
        }
    }
}
