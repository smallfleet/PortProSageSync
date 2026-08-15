using System.Text.Json;
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

            var invoices = (await client.GetInvoicesAsync(request, ct)).Invoices;
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
    /// Executes exactly one SyncRequest (read from a JSON file the caller wrote,
    /// same shape TriggerFileManager uses) and exits - never starts the Worker's
    /// polling loop, unlike host.Run(). This is the "Manual Run" path from the
    /// Admin app: distinct from dropping a file in TriggerFolder for an
    /// already-running Service to notice, this runs immediately in this one
    /// process regardless of whether anything else is running, and the caller can
    /// track/kill this specific process since it's a normal Process.Start result.
    /// Writes a matching *.result.json next to the request file so the caller
    /// (polling, same pattern as TriggerFileManager's processed-folder results)
    /// can read the outcome once this process exits.
    /// </summary>
    public static async Task<int> RunOnceAsync(string requestFilePath, IServiceProvider services, ILogger logger, CancellationToken ct)
    {
        if (!File.Exists(requestFilePath))
        {
            logger.LogError("FAILED: request file not found: {Path}", requestFilePath);
            return 1;
        }

        SyncRequest? request;
        try
        {
            // FileShare.ReadWrite, not File.ReadAllText's default (FileShare.Read) - reads
            // should never lock out a writer; see TriggerService.TryReadResult (Admin) for
            // the collision this same pattern caused elsewhere.
            using var stream = new FileStream(requestFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            request = JsonSerializer.Deserialize<SyncRequest>(reader.ReadToEnd());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "FAILED: could not parse request file {Path}", requestFilePath);
            return 1;
        }

        if (request is null)
        {
            logger.LogError("FAILED: request file {Path} deserialized to null.", requestFilePath);
            return 1;
        }

        logger.LogWarning("=== MANUAL RUN: executing one-time sync request {RequestId} ===", request.RequestId);

        var requestFolder = Path.GetDirectoryName(requestFilePath) ?? ".";
        var resultFilePath = Path.Combine(requestFolder, $"{request.RequestId}.result.json");
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        void WriteResultFile(SyncResult r) => WriteResultFileWithRetry(resultFilePath, JsonSerializer.Serialize(r, jsonOptions), logger);
        void WriteRequestFileFor(SyncRequest r) => File.WriteAllText(Path.Combine(requestFolder, $"{r.RequestId}.request.json"), JsonSerializer.Serialize(r, jsonOptions));
        void WriteResultFileFor(string id, SyncResult r) => WriteResultFileWithRetry(Path.Combine(requestFolder, $"{id}.result.json"), JsonSerializer.Serialize(r, jsonOptions), logger);

        var orchestrator = services.GetRequiredService<SyncOrchestrator>();
        // Checkpointed after every invoice (onProgress), not just once at the very
        // end - a Manual Run that's stopped (or crashes) mid-way used to leave
        // every count blank in History & Logs, same gap confirmed for the
        // Automatic Service's poll cycles. Each checkpoint overwrites the same
        // result file; the final write below (IsFinal=true) is just the last of
        // these overwrites if the run actually completes normally.
        var result = await orchestrator.RunAsync(request, ct, onProgress: WriteResultFile);

        WriteResultFile(result);

        logger.LogWarning(
            "MANUAL RUN complete: fetched={Fetched} imported={Imported} alreadyImported={AlreadyImported} " +
            "notFound={NotFound} zeroAmount={ZeroAmount} failedValidation={FailedVal} failedImport={FailedImp}",
            result.InvoicesFetched, result.InvoicesImported, result.InvoicesSkippedAlreadyImported,
            result.InvoicesNotFound, result.InvoicesSkippedZeroOrNegativeAmount, result.InvoicesFailedValidation, result.InvoicesFailedImport);

        // Every Manual Run gets its own automatic gap-fill follow-up too, not just
        // the Automatic Service's poll cycles - see GapFillRunner's doc comment.
        await GapFillRunner.RunIfApplicableAsync(request, result, orchestrator, WriteRequestFileFor, WriteResultFileFor, logger, ct);

        // Per-invoice detail no longer dumped here - SyncOrchestrator.RunAsync now logs
        // an "OUTCOME: ..." line for every invoice live, as it happens (see its doc
        // comment), so doing it again here after the fact would just duplicate every
        // line in the log. The Admin app's Warnings/Validation and Failed Transactions
        // views still work identically, since they filter the log for "VALIDATION:"/
        // "success=false" substrings, both still present in the live OUTCOME lines.

        return result.InvoicesFailedImport > 0 || result.InvoicesFailedValidation > 0 ? 1 : 0;
    }

    /// <summary>Writes a checkpoint/result file with FileShare.Read (so a concurrent
    /// reader - the Admin app polls this same file every 2 seconds - never blocks the
    /// write) plus a short retry-with-backoff for any remaining transient sharing
    /// conflict. Confirmed live 2026-08-12: File.WriteAllText's default exclusive
    /// sharing collided with the Admin app's own polling read on a 700+ invoice,
    /// 4-hour production run, and that single "being used by another process" error
    /// - on what is only ever a progress/telemetry write, not the actual sync work -
    /// aborted the entire run. The final attempt is allowed to throw normally, so a
    /// genuinely persistent problem (e.g. disk full) still surfaces as a real error.</summary>
    private static void WriteResultFileWithRetry(string path, string content, ILogger logger)
    {
        const int maxAttempts = 5;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                using var writer = new StreamWriter(stream);
                writer.Write(content);
                return;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                logger.LogWarning(
                    "Checkpoint write to {Path} hit a transient file-sharing conflict (attempt {Attempt}/{Max}) - retrying shortly.",
                    path, attempt, maxAttempts);
                Thread.Sleep(100 * attempt);
            }
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

            var matches = (await client.GetInvoicesAsync(lookup, ct)).Invoices;
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
    /// Real (non-DryRun) write of up to `count` invoices from `afterInvoiceNumber`
    /// onward (ascending, amount > 0 only - zero-amount ones don't count against
    /// the cap since SyncOrchestrator filters them out before the capped loop).
    /// Forces DryRun off in-memory for THIS PROCESS ONLY (never touches
    /// appsettings.Local.json or any environment variable) - this command never
    /// starts the Worker's automatic-poll loop, so there is no automatic run for
    /// that override to leak into; the process runs this one bounded batch and
    /// exits. Single pass, no separate discovery fetch - confirmed live 2026-08-05
    /// that computing a Start/End boundary from an earlier snapshot and handing
    /// it to a second, independently-refetched run doesn't reliably cap anything
    /// against PortPro's live data (see SyncRequest.MaxInvoicesToProcess). Uses
    /// an explicit invoice-number range (UseWatermark=false), so it never touches
    /// the persisted watermark/last-processed-invoice-number - call --set-anchor
    /// afterward to advance it to whatever was actually transferred.
    /// </summary>
    public static async Task<int> RealTransferAsync(string afterInvoiceNumber, int count, IServiceProvider services, ILogger logger, CancellationToken ct)
    {
        logger.LogWarning(
            "=== REAL TRANSFER: about to WRITE up to {Count} real invoice(s) to Sage 50, from '{After}' onward ===",
            count, afterInvoiceNumber);

        var sage50Settings = services.GetRequiredService<Sage50Settings>();
        if (sage50Settings.DryRun)
        {
            sage50Settings.DryRun = false;
            logger.LogWarning("DryRun forced to false in-memory for this process only - appsettings.Local.json and environment variables are untouched.");
        }

        var orchestrator = services.GetRequiredService<SyncOrchestrator>();

        try
        {
            var request = new SyncRequest
            {
                FilterType = FilterType.InvoiceNumberRange,
                StartInvoiceNumber = afterInvoiceNumber,
                MaxInvoicesToProcess = count,
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
    /// Reports the full range/list of everything recorded as imported so far - local
    /// state only, no PortPro/Sage 50 calls.
    /// </summary>
    public static int ReportImportedRange(IServiceProvider services, ILogger logger)
    {
        var state = services.GetRequiredService<SyncStateRepository>();
        var refs = state.GetAllImportedReferenceNumbers();

        if (refs.Count == 0)
        {
            logger.LogWarning("No invoices recorded as imported yet.");
            return 0;
        }

        logger.LogWarning("IMPORTED RANGE: {Count} invoice(s), from {Min} to {Max}.", refs.Count, refs[0], refs[refs.Count - 1]);
        logger.LogInformation("Full list: {List}", string.Join(", ", refs));
        return 0;
    }

    /// <summary>
    /// Removes false-positive imported_invoice records for a reference-number range -
    /// see SyncStateRepository.RemoveImportedInReferenceRange. PortPro/Sage 50 not
    /// touched at all; purely a local state correction.
    /// </summary>
    public static int ClearFalseImports(string startReferenceNumber, string endReferenceNumber, IServiceProvider services, ILogger logger)
    {
        var state = services.GetRequiredService<SyncStateRepository>();
        var removed = state.RemoveImportedInReferenceRange(startReferenceNumber, endReferenceNumber);
        logger.LogWarning("Cleared {Count} false-positive import record(s) in range {Start}..{End}.", removed, startReferenceNumber, endReferenceNumber);
        return 0;
    }

    /// <summary>
    /// Marks every PortPro invoice in [startReferenceNumber, endReferenceNumber]
    /// (ordinal, inclusive) as imported, using each invoice's own reference number
    /// as the recorded Sage 50 invoice number - for restoring local tracking after
    /// confirming directly in Sage 50 that invoices already exist for real (e.g.
    /// after CreateInvoiceAsync's return-value bug caused a false "nothing was
    /// saved" scare and the records were incorrectly cleared via
    /// --clear-false-imports). PortPro read-only; Sage 50 not touched.
    /// </summary>
    public static async Task<int> MarkImportedRangeAsync(string startReferenceNumber, string endReferenceNumber, IServiceProvider services, ILogger logger, CancellationToken ct)
    {
        var portPro = services.GetRequiredService<PortProClient>();
        var state = services.GetRequiredService<SyncStateRepository>();

        var lookup = new SyncRequest
        {
            FilterType = FilterType.InvoiceNumberRange,
            StartInvoiceNumber = startReferenceNumber,
            EndInvoiceNumber = endReferenceNumber,
            RequestedBy = "mark-imported-range"
        };

        var invoices = (await portPro.GetInvoicesAsync(lookup, ct)).Invoices;
        var marked = 0;
        foreach (var invoice in invoices)
        {
            state.MarkImported(invoice.Id, invoice.ReferenceNumber, invoice.ReferenceNumber);
            marked++;
        }

        logger.LogWarning("Marked {Count} invoice(s) as imported in range {Start}..{End}.", marked, startReferenceNumber, endReferenceNumber);
        return 0;
    }

    /// <summary>
    /// Fetches ONE invoice directly via PortPro's single-invoice endpoint
    /// (GET /invoices/{ref}) - bypassing the list/skip/limit endpoint entirely.
    /// Read-only; PortPro and Sage 50 are both untouched. Confirmed live
    /// 2026-08-12 as necessary: a real, existing invoice (RSRE_000284) never
    /// appeared via the list endpoint under any query (unfiltered, or
    /// date-bounded to its own invoice date), while a visually identical
    /// neighbor (RSRE_000283, same date, same status) did. This tells you
    /// whether the single-invoice endpoint can see it when the list endpoint
    /// can't - if it can, the problem is specific to how PortPro's list endpoint
    /// scopes results, not anything this project's pagination/filtering does; if
    /// it can't either, the invoice is inaccessible via PortPro's API entirely and
    /// worth raising with PortPro support directly.
    /// </summary>
    public static async Task<int> CheckInvoiceAsync(string referenceNumber, IServiceProvider services, ILogger logger, CancellationToken ct)
    {
        logger.LogInformation("=== CHECK INVOICE: fetching '{Ref}' directly via PortPro's single-invoice endpoint (list endpoint not touched) ===", referenceNumber);
        try
        {
            var portPro = services.GetRequiredService<PortProClient>();
            var invoice = await portPro.GetInvoiceAsync(referenceNumber, ct);

            if (invoice is null)
            {
                logger.LogWarning(
                    "NOT FOUND: PortPro's single-invoice endpoint also has no record of '{Ref}' (404). This invoice " +
                    "is not accessible via the API at all right now, not just missing from list queries - worth " +
                    "raising with PortPro support directly.",
                    referenceNumber);
                return 1;
            }

            // ReferenceNumber/Status/TotalAmount/BillingDate/Caller are populated directly
            // from this endpoint's own response - Id/CreatedAt/UpdatedAt are NOT (see
            // PortProInvoice's doc comments: those only get filled in by GetInvoicesAsync's
            // envelope-flattening, which this single-invoice call doesn't go through), so
            // deliberately not logged here to avoid implying they're always blank/missing.
            if (string.IsNullOrEmpty(invoice.ReferenceNumber))
            {
                var (rawStatus, rawBody) = await portPro.GetInvoiceRawAsync(referenceNumber, ct);
                logger.LogWarning(
                    "AMBIGUOUS: the single-invoice endpoint returned 200 OK for '{Ref}' but ReferenceNumber came " +
                    "back empty - this looks like a response-shape mismatch (this endpoint may wrap its response " +
                    "differently than PortProInvoice's flat shape), not confirmation the invoice is genuinely " +
                    "inaccessible. Raw response (status {RawStatus}):\n{RawBody}",
                    referenceNumber, rawStatus, rawBody);
                return 1;
            }

            // Best-effort extra diagnostic: how many charge sets/loads make up this
            // invoice ("data.total" in the raw response - see PortProSingleInvoiceResponse's
            // doc comment). Confirmed live 2026-08-12 this correlates with whether the list
            // endpoint can find the invoice at all (RSRE_000284, 2 charge sets from 2
            // different loads, was invisible to the list endpoint; RSRE_000283, 1 charge
            // set, was not) - checking whether that holds across more invoices.
            var chargeSetCount = "unknown";
            try
            {
                var (_, rawBody) = await portPro.GetInvoiceRawAsync(referenceNumber, ct);
                using var doc = JsonDocument.Parse(rawBody);
                if (doc.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("total", out var totalEl))
                {
                    chargeSetCount = totalEl.GetInt32().ToString();
                }
            }
            catch { /* best effort - diagnostic only, never fails the main result */ }

            logger.LogInformation(
                "FOUND: '{Ref}' exists via the single-invoice endpoint - status={Status}, billingDate={BillingDate}, " +
                "totalAmount={TotalAmount}, customer={Customer}, chargeSets={ChargeSetCount}. Since this succeeded " +
                "where the list endpoint didn't, the gap is specific to how PortPro's list endpoint scopes/returns " +
                "results, not this project's pagination or filtering.",
                invoice.ReferenceNumber, invoice.Status, invoice.BillingDate, invoice.TotalAmount,
                invoice.Caller?.CompanyName ?? invoice.CallerName, chargeSetCount);
            return 0;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "FAILED: could not check invoice '{Ref}'.", referenceNumber);
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
