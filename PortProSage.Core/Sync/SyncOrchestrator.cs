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

    public async Task<SyncResult> RunAsync(SyncRequest request, CancellationToken ct)
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
            request.From = result.WatermarkBeforeRun
                ?? DateTimeOffset.UtcNow.AddDays(-Math.Max(1, _syncSettings.InitialLookbackDays));
            request.To = DateTimeOffset.UtcNow;
        }

        _logger.LogInformation(
            "Starting sync {RequestId} ({FilterType}, UseWatermark={UseWatermark}, From={From}, To={To}, StartNo={StartNo}, EndNo={EndNo})",
            request.RequestId, request.FilterType, request.UseWatermark, request.From, request.To, request.StartInvoiceNumber, request.EndInvoiceNumber);

        try
        {
            await _sage50.ConnectAsync(ct);

            var invoices = await _portPro.GetInvoicesAsync(request, ct);
            result.InvoicesFetched = invoices.Count;

            // Only invoices with a positive total are eligible for import - a
            // zero/negative-amount invoice has nothing to post and is silently
            // skipped (not an error, not counted as imported).
            var zeroOrNegative = invoices.Where(i => i.TotalAmount <= 0m).ToList();
            if (zeroOrNegative.Count > 0)
            {
                result.InvoicesSkippedZeroOrNegativeAmount = zeroOrNegative.Count;
                foreach (var skipped in zeroOrNegative)
                {
                    _logger.LogInformation(
                        "Skipping invoice {Ref} (id={Id}) - total amount {Amount} is not greater than zero.",
                        skipped.ReferenceNumber, skipped.Id, skipped.TotalAmount);
                }
                invoices = invoices.Where(i => i.TotalAmount > 0m).ToList();
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

            foreach (var invoice in orderedInvoices)
            {
                ct.ThrowIfCancellationRequested();
                var outcome = await ProcessOneInvoiceAsync(invoice, ct);
                result.Outcomes.Add(outcome);

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
                        result.InvoicesSkippedAlreadyImported++;
                    else
                        result.InvoicesImported++;
                }
                else if (outcome.Messages.Any(m => m.StartsWith("VALIDATION:")))
                {
                    result.InvoicesFailedValidation++;
                }
                else
                {
                    result.InvoicesFailedImport++;
                }

                // Only a watermark-driven run ("continue from where we left off")
                // advances persisted state - an explicit range is a one-time override
                // that leaves both markers exactly as they were, so the next
                // no-range run picks up from here, not from the explicit range's edge.
                // Advanced immediately after EACH invoice (success or failure - a
                // permanently-failing invoice must not be re-attempted forever), not
                // batched until the end of the loop: if the process is killed mid-run
                // (see Sage50Client.TerminateOnFatalWriteError), everything already
                // handled before the failure must already be durably anchored, and
                // nothing after it should be. SetLastChangedWatermark/
                // SetLastProcessedInvoiceNumber only ever move forward, so this is
                // safe even if a value here were ever out of order.
                if (request.UseWatermark)
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

                if (request.MaxInvoicesToProcess is not null && result.Outcomes.Count >= request.MaxInvoicesToProcess.Value)
                {
                    _logger.LogInformation(
                        "Reached MaxInvoicesToProcess={Max} - stopping this run early; {Remaining} more eligible invoice(s) were fetched but not processed.",
                        request.MaxInvoicesToProcess.Value, orderedInvoices.Count - result.Outcomes.Count);
                    break;
                }
            }

            if (request.UseWatermark && orderedInvoices.Count > 0 && request.To is not null)
            {
                // Also fold in the run's own "to" boundary so the date watermark
                // covers the whole fetched window, not just up to the last invoice's
                // own updatedAt (which may be earlier than "to" if nothing changed
                // right at the edge of the window).
                _state.SetLastChangedWatermark(request.To.Value);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync {RequestId} failed", request.RequestId);
            result.Outcomes.Add(new InvoiceProcessingOutcome
            {
                Success = false,
                Messages = { $"FATAL: {ex.Message}" }
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

        return result;
    }

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
