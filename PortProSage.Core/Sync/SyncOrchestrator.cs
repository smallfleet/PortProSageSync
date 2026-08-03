using Microsoft.Extensions.Logging;
using PortProSage.Core.Data;
using PortProSage.Core.Models;
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
    private readonly ILogger<SyncOrchestrator> _logger;

    public SyncOrchestrator(
        PortProClient portPro,
        ISage50Client sage50,
        InvoiceValidationService validator,
        SyncStateRepository state,
        ILogger<SyncOrchestrator> logger)
    {
        _portPro = portPro;
        _sage50 = sage50;
        _validator = validator;
        _state = state;
        _logger = logger;
    }

    public async Task<SyncResult> RunAsync(SyncRequest request, CancellationToken ct)
    {
        var result = new SyncResult
        {
            RequestId = request.RequestId,
            StartedAtUtc = DateTimeOffset.UtcNow
        };

        _logger.LogInformation(
            "Starting sync {RequestId} ({FilterType}, From={From}, To={To}, StartNo={StartNo}, EndNo={EndNo})",
            request.RequestId, request.FilterType, request.From, request.To, request.StartInvoiceNumber, request.EndInvoiceNumber);

        try
        {
            await _sage50.ConnectAsync(ct);

            var invoices = await _portPro.GetInvoicesAsync(request, ct);
            result.InvoicesFetched = invoices.Count;

            foreach (var invoice in invoices)
            {
                ct.ThrowIfCancellationRequested();
                var outcome = await ProcessOneInvoiceAsync(invoice, ct);
                result.Outcomes.Add(outcome);

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
            }

            // Advance the watermark only for the automatic "last changed date" flow,
            // and only up to the latest updatedAt actually seen, so a slow-running
            // batch can't skip invoices changed mid-run.
            if (request.FilterType == FilterType.LastChangedDate && invoices.Count > 0)
            {
                var maxUpdatedAt = invoices.Max(i => i.UpdatedAt) ?? request.To;
                if (maxUpdatedAt is not null)
                {
                    _state.SetLastChangedWatermark(maxUpdatedAt.Value);
                }
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

        result.FinishedAtUtc = DateTimeOffset.UtcNow;
        _logger.LogInformation(
            "Finished sync {RequestId}: fetched={Fetched} imported={Imported} skipped={Skipped} failedValidation={FailedVal} failedImport={FailedImp}",
            request.RequestId, result.InvoicesFetched, result.InvoicesImported, result.InvoicesSkippedAlreadyImported,
            result.InvoicesFailedValidation, result.InvoicesFailedImport);

        return result;
    }

    private async Task<InvoiceProcessingOutcome> ProcessOneInvoiceAsync(PortProInvoice invoice, CancellationToken ct)
    {
        var outcome = new InvoiceProcessingOutcome
        {
            PortProInvoiceId = invoice.Id,
            ReferenceNumber = invoice.ReferenceNumber
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
            if (!decimal.TryParse(line.FinalAmount, out var amount))
            {
                amount = 0m;
            }

            var itemCode = validation.ResolvedItemCodesByChargeName.TryGetValue(line.Name, out var code)
                ? code
                : line.Name;

            sageInvoice.Lines.Add(new Sage50InvoiceLine
            {
                ItemCode = itemCode,
                Description = line.Name,
                Quantity = 1,
                UnitPrice = amount,
                RevenueAccount = !string.IsNullOrWhiteSpace(line.GlCode) ? line.GlCode! : validation.ResolvedRevenueAccount ?? string.Empty
            });
        }

        return sageInvoice;
    }
}
