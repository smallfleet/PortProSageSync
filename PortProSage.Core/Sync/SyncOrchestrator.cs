using Microsoft.Extensions.Logging;
using PortProSage.Core.Config;
using PortProSage.Core.Data;
using PortProSage.Core.Models;
using PortProSage.Core.Notifications;
using PortProSage.Core.PortPro;
using PortProSage.Core.Sage50;
using PortProSage.Core.Validation;

namespace PortProSage.Core.Sync;

public class SyncOrchestrator
{
    private readonly PortProClient _portPro;
    private readonly ISage50Client _sage50;
    private readonly InvoiceValidationService _validator;
    private readonly SyncStateRepository _state;
    private readonly EmailService _email;
    private readonly SyncSettings _syncSettings;
    private readonly ILogger<SyncOrchestrator> _logger;

    public SyncOrchestrator(
        PortProClient portPro,
        ISage50Client sage50,
        InvoiceValidationService validator,
        SyncStateRepository state,
        EmailService email,
        SyncSettings syncSettings,
        ILogger<SyncOrchestrator> logger)
    {
        _portPro = portPro;
        _sage50 = sage50;
        _validator = validator;
        _state = state;
        _email = email;
        _syncSettings = syncSettings;
        _logger = logger;
    }

    /// <param name="onProgress">Invoked after every invoice is processed (and
    /// before the invocation returns to the caller, so it's safe to write the
    /// result to disk synchronously from inside the callback) - result.IsFinal is
    /// always false during these calls, true only on the SyncResult this method
    /// ultimately returns. Lets the caller checkpoint real progress to disk as the
    /// run proceeds, instead of only ever writing a result once at the very end -
    /// see SyncResult.IsFinal's doc comment for why that gap mattered.</param>
    public async Task<SyncResult> RunAsync(SyncRequest request, CancellationToken ct, Action<SyncResult>? onProgress = null)
    {
        var result = new SyncResult
        {
            RequestId = request.RequestId,
            StartedAtUtc = DateTimeOffset.UtcNow,
            ProcessId = System.Diagnostics.Process.GetCurrentProcess().Id
        };

        // Pre-image of the persisted "continue from" state - captured before
        // anything below can change it, so it can be compared against the
        // post-image captured at the very end of this method (see
        // SyncResult.WatermarkBeforeRun's doc comment for why this is captured
        // unconditionally, not just for watermark-driven runs).
        result.WatermarkBeforeRun = _state.GetLastChangedWatermark();
        result.LastProcessedInvoiceNumberBeforeRun = _state.GetLastProcessedInvoiceNumber();

        // "Continue from where we left off" (no explicit range given, whether this
        // is the automatic poll or a manual trigger run with no --mode) - resolve
        // From/To from the persisted date watermark. An explicit range (UseWatermark
        // false) uses exactly what the caller specified and never touches this or
        // any other persisted state - see SyncRequest.UseWatermark's doc comment.
        if (request.UseWatermark)
        {
            // Upper bound capped at "now minus ProcessingDelayDays", not raw "now" -
            // see SyncSettings.ProcessingDelayDays's doc comment. The watermark
            // never advances past this capped bound (below), so a held-back
            // invoice is simply picked up on a later run once it clears the delay.
            var upperBound = DateTimeOffset.UtcNow.AddDays(-Math.Max(0, _syncSettings.ProcessingDelayDays));
            var lowerBound = result.WatermarkBeforeRun ?? upperBound.AddDays(-Math.Max(1, _syncSettings.ProcessingDelayDays));

            // The persisted watermark can already sit at or past the delay-capped
            // upper bound - e.g. ProcessingDelayDays was raised (or the watermark
            // had already advanced under a smaller value) since the last run.
            // Clamp rather than send PortPro an inverted From > To window: there is
            // genuinely nothing eligible yet, which is a normal, temporary state
            // (it self-resolves once real time closes the gap), not an error.
            request.From = lowerBound > upperBound ? upperBound : lowerBound;
            request.To = upperBound;
        }

        _logger.LogInformation(
            "Starting sync {RequestId} ({FilterType}, UseWatermark={UseWatermark}, From={From}, To={To}, StartNo={StartNo}, EndNo={EndNo})",
            request.RequestId, request.FilterType, request.UseWatermark, request.From, request.To, request.StartInvoiceNumber, request.EndInvoiceNumber);

        // Gap scan: rewrites itself into an InvoiceNumberList request before anything
        // else runs, so everything downstream (fetch-one-at-a-time via the single-
        // invoice endpoint, validation, import, checkpointing) is the exact same,
        // already-proven code path InvoiceNumberList mode uses - this step's only job
        // is producing that candidate list. See ReferenceNumberFormat's doc comment
        // for the prefix/number/suffix split (generic, not hardcoded to any one
        // client's format).
        if (request.FilterType == FilterType.InvoiceNumberGapScan)
        {
            if (!ReferenceNumberFormat.TryParse(request.StartInvoiceNumber, out var startPrefix, out var startNumber, out var width, out var startSuffix) ||
                !ReferenceNumberFormat.TryParse(request.EndInvoiceNumber, out var endPrefix, out var endNumber, out _, out var endSuffix))
            {
                throw new InvalidOperationException(
                    $"Gap scan needs a valid Start/End invoice number (prefix + number, e.g. RSRE_000100) - got " +
                    $"Start='{request.StartInvoiceNumber}', End='{request.EndInvoiceNumber}'.");
            }
            if (!string.Equals(startPrefix, endPrefix, StringComparison.Ordinal) ||
                !string.Equals(startSuffix, endSuffix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Gap scan's Start ('{request.StartInvoiceNumber}') and End ('{request.EndInvoiceNumber}') must " +
                    "share the same prefix/suffix pattern, just different numbers.");
            }
            if (endNumber < startNumber)
            {
                throw new InvalidOperationException($"Gap scan's End number ({endNumber}) is before Start ({startNumber}).");
            }

            var alreadyImported = new HashSet<string>(_state.GetAllImportedReferenceNumbers(), StringComparer.OrdinalIgnoreCase);
            var candidates = new List<string>();
            for (var n = startNumber; n <= endNumber; n++)
            {
                var candidate = ReferenceNumberFormat.Format(startPrefix, n, width, startSuffix);
                if (!alreadyImported.Contains(candidate))
                {
                    candidates.Add(candidate);
                }
            }

            var totalInRange = endNumber - startNumber + 1;
            _logger.LogInformation(
                "Gap scan {RequestId}: range {Start}..{End} ({Total} number(s)), {AlreadyImported} already imported " +
                "(skipped without a PortPro call), {ToCheck} candidate(s) to check individually via PortPro's " +
                "single-invoice endpoint.",
                request.RequestId, request.StartInvoiceNumber, request.EndInvoiceNumber, totalInRange,
                totalInRange - candidates.Count, candidates.Count);

            request.FilterType = FilterType.InvoiceNumberList;
            request.InvoiceNumberList = string.Join(",", candidates);
        }

        // Recorded on the result (not just the request) so it survives into
        // History & Logs regardless of run type - both a manually-typed list and a
        // gap scan's computed candidate list end up here, since gap scan rewrites
        // itself into this same filter type just above.
        if (request.FilterType == FilterType.InvoiceNumberList)
        {
            result.ResolvedInvoiceNumberList = request.InvoiceNumberList;
        }

        // Nothing eligible yet (see the clamp above) - skip the Sage50 connect and
        // PortPro fetch entirely rather than asking for a zero-width window every
        // cycle, and say so plainly instead of leaving a bare "fetched=0" with no
        // explanation.
        if (request.UseWatermark && request.From is not null && request.To is not null && request.From >= request.To)
        {
            _logger.LogInformation(
                "Nothing to process yet for {RequestId} - the watermark ({From}) has already caught up to the " +
                "processing-delay upper bound ({To}, now minus {Days} day(s)). Skipping this cycle.",
                request.RequestId, request.From, request.To, _syncSettings.ProcessingDelayDays);
            result.WatermarkAfterRun = result.WatermarkBeforeRun;
            result.LastProcessedInvoiceNumberAfterRun = result.LastProcessedInvoiceNumberBeforeRun;
            result.FinishedAtUtc = DateTimeOffset.UtcNow;
            result.IsFinal = true;
            return result;
        }

        try
        {
            await _sage50.ConnectAsync(ct);

            // Captured once, before the batch loop below starts overwriting
            // request.From/To with each batch's own narrower sub-window - see
            // SyncResult.EffectiveFromUtc's doc comment.
            result.EffectiveFromUtc = request.From;
            result.EffectiveToUtc = request.To;

            var batches = ComputeBatches(request.From, request.To);
            result.BatchCount = batches.Count;
            var hitMaxCap = false;

            // Set the moment ANY invoice in this run fails (validation or write) -
            // once true, the watermark stops advancing for every invoice/batch after
            // that point too, even ones that succeed. Invoices are processed in
            // ascending order and the watermark is a single "everything up to here is
            // durably done" marker with no concept of a gap - letting a LATER success
            // advance it past an EARLIER failure would mean that failure never gets
            // retried on the next run. The failed invoice (and everything after it)
            // simply gets re-fetched and re-attempted next time; anything that
            // already succeeded this run is safely re-skipped via IsAlreadyImported
            // (tracked by PortPro invoice id, independent of the watermark), so
            // nothing is double-posted by retrying past the failure point.
            var watermarkBlocked = false;

            for (var batchIndex = 0; batchIndex < batches.Count; batchIndex++)
            {
                var (batchFrom, batchTo) = batches[batchIndex];
                request.From = batchFrom;
                request.To = batchTo;

                _logger.LogInformation(
                    "Batch {BatchNum} of {BatchTotal} for {RequestId}: {From} to {To}",
                    batchIndex + 1, batches.Count, request.RequestId,
                    batchFrom?.ToString("yyyy-MM-dd HH:mm") ?? "(no date filter)",
                    batchTo?.ToString("yyyy-MM-dd HH:mm") ?? "(no date filter)");

                var invoices = await _portPro.GetInvoicesAsync(request, ct);
                var batchFetched = invoices.Count;
                result.InvoicesFetched += batchFetched;

                // Only invoices with a positive total are eligible for import - a
                // zero/negative-amount invoice has nothing to post and is silently
                // skipped (not an error, not counted as imported).
                var zeroOrNegative = invoices.Where(i => i.TotalAmount <= 0m).ToList();
                if (zeroOrNegative.Count > 0)
                {
                    result.InvoicesSkippedZeroOrNegativeAmount += zeroOrNegative.Count;
                    foreach (var skipped in zeroOrNegative)
                    {
                        _logger.LogInformation(
                            "Skipping invoice {Ref} (id={Id}) - total amount {Amount} is not greater than zero.",
                            skipped.ReferenceNumber, skipped.Id, skipped.TotalAmount);
                    }
                    invoices = invoices.Where(i => i.TotalAmount > 0m).ToList();
                }

                // Invoices dated before the configured cutoff (if any) are never
                // attempted - see SyncSettings.CutoffInvoiceDate's doc comment for why:
                // this is what actually stops the run-killing Sage 50 rejection at the
                // source, instead of hitting it mid-write.
                var batchBeforeCutoff = 0;
                if (_syncSettings.CutoffInvoiceDate is { } cutoff)
                {
                    var tooOld = invoices.Where(i => (i.BillingDate ?? i.CompletedDate) is { } d && d < cutoff).ToList();
                    batchBeforeCutoff = tooOld.Count;
                    if (tooOld.Count > 0)
                    {
                        result.InvoicesSkippedBeforeCutoff += tooOld.Count;
                        foreach (var skipped in tooOld)
                        {
                            _logger.LogInformation(
                                "Skipping invoice {Ref} (id={Id}) - dated {Date}, before the configured cutoff {Cutoff}.",
                                skipped.ReferenceNumber, skipped.Id, skipped.BillingDate ?? skipped.CompletedDate, cutoff);
                        }
                        invoices = invoices.Except(tooOld).ToList();
                    }
                }

                // Process strictly in ascending invoice-number order (ordinal string
                // comparison - correctly orders this account's consistent "PREFIX_NNNNNN"
                // reference numbers) so the anchor updated below is always monotonic
                // within a run: once N is processed, every invoice still to come is
                // guaranteed to be numbered higher than N. This is what makes it safe to
                // advance the anchor per-invoice instead of only once at the end of the
                // whole batch.
                var orderedInvoices = invoices
                    .OrderBy(i => i.ReferenceNumber, StringComparer.Ordinal)
                    .ToList();

                var batchImported = 0;
                var batchAlreadyImported = 0;
                var batchFailedValidation = 0;
                var batchFailedImport = 0;

                foreach (var invoice in orderedInvoices)
                {
                    ct.ThrowIfCancellationRequested();
                    var outcome = await ProcessOneInvoiceAsync(invoice, ct);
                    result.Outcomes.Add(outcome);

                    // Every outcome (success, failure, skip - not just genuine transfers,
                    // see TRANSFER below) gets its own durable, immediate log line, not
                    // just an entry in result.Outcomes - confirmed live 2026-08-12 that
                    // result.json is a single file OVERWRITTEN whole on every checkpoint,
                    // so one bad write (e.g. disk full mid-write) can silently destroy
                    // EVERY earlier checkpointed outcome too, not just the newest one. The
                    // log file, by contrast, is append-only - one failed write can't erase
                    // lines already flushed to disk. The Admin app's "Per-invoice outcomes"
                    // tab now falls back to parsing these lines (LogExtractorService.
                    // ExtractOutcomes) whenever result.json comes back null or empty, the
                    // same recovery path "Invoice Transferred" (TRANSFER: below) already had.
                    _logger.LogInformation(
                        "OUTCOME: Ref={Ref} PortProDate={PortProDate} Success={Success} Sage50Number={SageNo} Messages=[{Messages}]",
                        outcome.ReferenceNumber, outcome.PortProInvoiceDate?.ToString("yyyy-MM-dd") ?? "(none)",
                        outcome.Success, outcome.Sage50InvoiceNumber ?? "(none)", string.Join(" | ", outcome.Messages));

                    // A single, consistently-formatted line per genuinely-transferred invoice
                    // (skips ALREADY_IMPORTED/DRYRUN, which didn't actually post anything this
                    // run) - logged here, inside RunAsync itself, so both the automatic poll
                    // (Worker.cs) and manual/diagnostic runs (Diagnostics.cs) produce it
                    // identically without each caller having to remember to. The Admin app's
                    // "Invoice Transferred" tab parses this line back out of the full log
                    // rather than reading result.json, since the automatic poll never writes
                    // a result.json at all - the log is the only record that exists for it.
                    if (outcome.Success && outcome.Sage50InvoiceNumber is not null &&
                        outcome.Sage50InvoiceNumber != "ALREADY_IMPORTED" &&
                        !outcome.Sage50InvoiceNumber.StartsWith("DRYRUN-", StringComparison.Ordinal))
                    {
                        _logger.LogInformation(
                            "TRANSFER: Ref={Ref} Sage50Number={SageNo} PortProDate={PortProDate} Sage50Date={Sage50Date} TotalAmount={TotalAmount} TaxCharged={TaxCharged}",
                            outcome.ReferenceNumber, outcome.Sage50InvoiceNumber,
                            outcome.PortProInvoiceDate?.ToString("yyyy-MM-dd") ?? "(none)",
                            outcome.Sage50InvoiceDate?.ToString("yyyy-MM-dd") ?? "(none)",
                            outcome.TotalAmount, outcome.TaxCharged);
                    }

                    if (outcome.Success)
                    {
                        if (outcome.Sage50InvoiceNumber == "ALREADY_IMPORTED")
                        {
                            result.InvoicesSkippedAlreadyImported++;
                            batchAlreadyImported++;
                        }
                        else
                        {
                            result.InvoicesImported++;
                            batchImported++;
                        }
                    }
                    else if (outcome.Messages.Any(m => m.StartsWith("VALIDATION:")))
                    {
                        result.InvoicesFailedValidation++;
                        batchFailedValidation++;
                        watermarkBlocked = true;
                    }
                    else
                    {
                        result.InvoicesFailedImport++;
                        batchFailedImport++;
                        watermarkBlocked = true;
                    }

                    // Only a watermark-driven run ("continue from where we left off")
                    // advances persisted state - an explicit range is a one-time override
                    // that leaves both markers exactly as they were, so the next
                    // no-range run picks up from here, not from the explicit range's edge.
                    // Advanced immediately after EACH invoice (success OR already-imported
                    // - either one means this invoice has been genuinely read and settled,
                    // not just attempted), not batched until the end of the loop: if the
                    // process is killed mid-run (see Sage50Client.TerminateOnFatalWriteError),
                    // everything already settled before that must already be durably
                    // anchored. Skipped once watermarkBlocked is set (see its doc comment) -
                    // a failed invoice, and everything processed after it in this same run,
                    // must NOT be anchored past, so the failure gets retried next time
                    // instead of being silently skipped forever. SetLastChangedWatermark/
                    // SetLastProcessedInvoiceNumber only ever move forward, so this is
                    // safe even if a value here were ever out of order.
                    if (request.UseWatermark && !watermarkBlocked)
                    {
                        if (invoice.UpdatedAt is not null)
                        {
                            _state.SetLastChangedWatermark(invoice.UpdatedAt.Value);
                        }

                        if (!string.IsNullOrEmpty(invoice.ReferenceNumber))
                        {
                            _state.SetLastProcessedInvoiceNumber(invoice.ReferenceNumber);
                            result.LastProcessedInvoiceNumberAfterRun = invoice.ReferenceNumber;
                        }
                    }

                    // Checkpoint after every invoice - see the onProgress parameter's
                    // doc comment. FinishedAtUtc doubles as "as of" for a checkpoint
                    // that never becomes final, so a caller/viewer has a meaningful
                    // timestamp even without IsFinal ever being set.
                    //
                    // Never allowed to fail the run - a checkpoint write is telemetry,
                    // not the actual work. Confirmed live 2026-08-12: an uncaught
                    // sharing-violation exception here (Diagnostics.WriteResultFile
                    // colliding with the Admin app's own polling read) aborted an
                    // otherwise-healthy 700+ invoice run, mid-batch, over nothing more
                    // than a momentary file lock on a progress file - a genuine Sage 50/
                    // PortPro failure should be the only thing that can do that.
                    result.FinishedAtUtc = DateTimeOffset.UtcNow;
                    try
                    {
                        onProgress?.Invoke(result);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Checkpoint write failed for sync {RequestId} after invoice {Ref} - continuing the run regardless.",
                            request.RequestId, invoice.ReferenceNumber);
                    }

                    if (request.MaxInvoicesToProcess is not null && result.Outcomes.Count >= request.MaxInvoicesToProcess.Value)
                    {
                        _logger.LogInformation(
                            "Reached MaxInvoicesToProcess={Max} - stopping this run early; {Remaining} more eligible invoice(s) were fetched but not processed.",
                            request.MaxInvoicesToProcess.Value, orderedInvoices.Count - result.Outcomes.Count);
                        hitMaxCap = true;
                        break;
                    }
                }

                // Commit this batch's watermark now, before moving to the next one -
                // NOT gated on "did this batch actually have any invoices" (a batch
                // that was successfully fetched and checked is fully accounted for
                // regardless of what it contained, so a genuinely-empty day shouldn't
                // be left to be re-fetched forever) but IS gated on watermarkBlocked
                // (see its doc comment) - once any invoice in this run has failed, no
                // later batch boundary gets to commit past it either, or the failure
                // would never be retried. If the process dies on batch 3 of 10 with
                // no failures yet, batches 1-2 are already durably committed and only
                // batch 3 onward needs to be retried next time.
                if (request.UseWatermark && batchTo is not null && !watermarkBlocked)
                {
                    _state.SetLastChangedWatermark(batchTo.Value);
                }

                _logger.LogInformation(
                    "Batch {BatchNum} of {BatchTotal} complete: fetched={Fetched} imported={Imported} alreadyImported={AlreadyImported} " +
                    "zeroAmount={ZeroAmount} beforeCutoff={BeforeCutoff} failedValidation={FailedValidation} failedImport={FailedImport}",
                    batchIndex + 1, batches.Count, batchFetched, batchImported, batchAlreadyImported,
                    zeroOrNegative.Count, batchBeforeCutoff, batchFailedValidation, batchFailedImport);

                if (hitMaxCap) break;
            }

            // Restore the overall window, not whichever batch happened to run last -
            // nothing in this method reads request.From/To again, but leaving the
            // shared request object sitting on the final batch's narrow sub-window
            // would be a surprise to any future caller/reader.
            request.From = result.EffectiveFromUtc;
            request.To = result.EffectiveToUtc;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync {RequestId} failed", request.RequestId);

            // Include the innermost exception's own message too, not just the
            // outer wrapper - confirmed live 2026-08-10: a Sage 50 "already
            // open under this username" failure surfaced here as just the
            // generic outer InvalidOperationException text ("Failed to open
            // the Sage 50 company file. Verify..."), while the actually
            // diagnostic text ("Someone else is already using the program
            // under this name...") was buried in the inner
            // SimplyErrorMessageException, visible only by opening the raw
            // log file. History & Logs reads this Messages list directly, so
            // the specific cause needs to be in there, not just a generic
            // troubleshooting checklist.
            var innermost = ex;
            while (innermost.InnerException is not null) innermost = innermost.InnerException;
            var detail = ReferenceEquals(innermost, ex) ? ex.Message : $"{ex.Message} ---> {innermost.Message}";

            result.Outcomes.Add(new InvoiceProcessingOutcome
            {
                Success = false,
                Messages = { $"FATAL: {detail}" }
            });
        }

        // Post-image, read fresh from state rather than trusting whatever the loop
        // above set - always populated regardless of UseWatermark, so an explicit
        // range's "this should be unchanged" guarantee is directly checkable
        // (WatermarkBeforeRun == WatermarkAfterRun) rather than just documented.
        result.WatermarkAfterRun = _state.GetLastChangedWatermark();
        result.LastProcessedInvoiceNumberAfterRun = _state.GetLastProcessedInvoiceNumber();

        result.FinishedAtUtc = DateTimeOffset.UtcNow;
        _logger.LogInformation(
            "Finished sync {RequestId}: fetched={Fetched} imported={Imported} alreadyImported={Skipped} zeroAmount={ZeroAmount} failedValidation={FailedVal} failedImport={FailedImp}",
            request.RequestId, result.InvoicesFetched, result.InvoicesImported, result.InvoicesSkippedAlreadyImported,
            result.InvoicesSkippedZeroOrNegativeAmount, result.InvoicesFailedValidation, result.InvoicesFailedImport);

        // Every run with at least one failure (automatic or manual - not just manual
        // triggers, which are the only path that already writes a result.json) gets
        // a CSV of the failures and an email notification. A failure writing/emailing
        // this report must never fail the sync itself - the sync's own outcome is
        // already final by this point.
        try
        {
            var reportPath = FailedTransactionReport.WriteIfAnyFailures(result, _syncSettings.FailedTransactionsFolder);
            if (reportPath is not null)
            {
                await _email.SendFailedTransactionsAsync(reportPath, result, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write/email the failed-transactions report for sync {RequestId}", request.RequestId);
        }

        // Checked at the end of every run, not on a separate timer - see
        // LogRetentionService's doc comment. No-op unless LogRetentionDays is
        // explicitly set above 0; a cleanup failure (e.g. today's file still
        // locked) must never fail the sync itself.
        try
        {
            LogRetentionService.CleanupOldLogs(_syncSettings, _logger);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Log retention cleanup failed for sync {RequestId}", request.RequestId);
        }

        // Only ever set true here, on the run's genuine last write - every
        // checkpoint above (onProgress) left it false. Callers persist this
        // returned result as their own final write.
        result.IsFinal = true;
        return result;
    }

    /// <summary>Always a single batch covering the whole window - splitting by day
    /// was removed 2026-08-14 (added complexity - per-batch watermark commits,
    /// "Batch N of M" logging - without pulling its weight). SyncResult.BatchCount
    /// is always 1 now, kept as a field rather than removed outright since History
    /// & Logs still reads it.</summary>
    private static List<(DateTimeOffset? From, DateTimeOffset? To)> ComputeBatches(DateTimeOffset? from, DateTimeOffset? to)
        => new() { (from, to) };

    private async Task<InvoiceProcessingOutcome> ProcessOneInvoiceAsync(PortProInvoice invoice, CancellationToken ct)
    {
        var outcome = new InvoiceProcessingOutcome
        {
            PortProInvoiceId = invoice.Id,
            ReferenceNumber = invoice.ReferenceNumber,
            PortProInvoiceDate = invoice.BillingDate ?? invoice.CompletedDate,
            TotalAmount = invoice.TotalAmount,
            TaxCharged = invoice.Pricing
                .Where(l => InvoiceValidationService.TryGetTaxAbbreviation(l.Name, out _))
                .Sum(l => decimal.TryParse(l.FinalAmount, out var amount) ? amount : 0m)
        };

        if (_state.IsAlreadyImported(invoice.Id))
        {
            outcome.Success = true;
            outcome.Sage50InvoiceNumber = "ALREADY_IMPORTED";
            outcome.Messages.Add("Skipped - already imported in a previous run.");
            return outcome;
        }

        var validation = await _validator.ValidateAsync(invoice, ct);
        outcome.Messages.AddRange(validation.Warnings);

        if (!validation.IsValid)
        {
            outcome.Success = false;
            outcome.Messages.AddRange(validation.Errors.Select(e => $"VALIDATION: {e}"));
            return outcome;
        }

        try
        {
            var sageInvoice = MapToSage50Invoice(invoice, validation);
            outcome.Sage50InvoiceDate = sageInvoice.InvoiceDate;
            var sageInvoiceNumber = await _sage50.CreateInvoiceAsync(sageInvoice, ct);

            // A dry-run invoice number (see Sage50Client) must NOT be recorded as
            // permanently imported - otherwise flipping DryRun off later would skip
            // these invoices forever, thinking they were already imported for real.
            if (!sageInvoiceNumber.StartsWith("DRYRUN-", StringComparison.Ordinal))
            {
                _state.MarkImported(invoice.Id, invoice.ReferenceNumber, sageInvoiceNumber);
            }

            outcome.Success = true;
            outcome.Sage50InvoiceNumber = sageInvoiceNumber;
            outcome.Messages.Add(sageInvoiceNumber.StartsWith("DRYRUN-", StringComparison.Ordinal)
                ? $"DRY RUN - would import as {sageInvoiceNumber} (not recorded; re-runs will re-validate this invoice)."
                : $"Imported as Sage 50 invoice {sageInvoiceNumber}.");
        }
        catch (DuplicateInvoiceNumberException ex)
        {
            // Sage 50 already has this exact invoice number for this customer - not
            // a failure, just evidence it was posted in an earlier run (e.g. the
            // original incident's cascade). Record it as imported and move on
            // instead of treating this as fatal - see DuplicateInvoiceNumberException.
            _logger.LogWarning(
                "SKIPPED invoice {Ref} (PortPro id {Id}): {Message} Marking as imported without re-posting.",
                invoice.ReferenceNumber, invoice.Id, ex.Message);
            _state.MarkImported(invoice.Id, invoice.ReferenceNumber, invoice.ReferenceNumber);
            outcome.Success = true;
            outcome.Sage50InvoiceNumber = invoice.ReferenceNumber;
            outcome.Messages.Add($"SKIPPED - already existed in Sage 50 under this invoice number: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import PortPro invoice {Id} ({Ref}) into Sage 50", invoice.Id, invoice.ReferenceNumber);
            outcome.Success = false;
            outcome.Messages.Add($"IMPORT ERROR: {ex.Message}");
        }

        return outcome;
    }

    private static Sage50Invoice MapToSage50Invoice(PortProInvoice invoice, ValidationResult validation)
    {
        var sageInvoice = new Sage50Invoice
        {
            ExternalReference = invoice.ReferenceNumber,
            CustomerCode = validation.ResolvedSage50CustomerCode!,
            InvoiceDate = (invoice.BillingDate ?? invoice.CompletedDate ?? DateTimeOffset.UtcNow).UtcDateTime.Date
        };

        foreach (var line in invoice.Pricing)
        {
            // Lines absent here were resolved as a tax charge (see ValidationResult.
            // ResolvedTaxCode/InvoiceValidationService.TryGetTaxAbbreviation) - they
            // aren't real line items, so they don't become an invoice line at all;
            // the resolved tax code is applied to the real revenue lines below instead.
            if (!validation.ResolvedItemCodesByChargeName.TryGetValue(line.Name, out var itemCode))
            {
                continue;
            }

            if (!decimal.TryParse(line.FinalAmount, out var amount))
            {
                amount = 0m;
            }

            // Use the account validation actually resolved and confirmed exists for
            // THIS charge (not PortPro's raw glCode again here) - re-deriving from
            // glCode independently at this point would bypass whatever
            // TryResolveAccountForCharge decided and could post to an account that
            // was never checked against Sage 50's chart of accounts at all.
            validation.ResolvedRevenueAccountByChargeName.TryGetValue(line.Name, out var revenueAccount);

            sageInvoice.Lines.Add(new Sage50InvoiceLine
            {
                ItemCode = itemCode,
                Description = line.Name,
                Quantity = 1,
                UnitPrice = amount,
                RevenueAccount = revenueAccount ?? string.Empty,
                TaxCode = validation.ResolvedTaxCode
            });
        }

        return sageInvoice;
    }
}
