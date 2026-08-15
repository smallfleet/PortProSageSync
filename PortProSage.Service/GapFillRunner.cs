using Microsoft.Extensions.Logging;
using PortProSage.Core.Models;
using PortProSage.Core.PortPro;
using PortProSage.Core.Sync;

namespace PortProSage.Service;

/// <summary>
/// Shared by Manual Run (Diagnostics.RunOnceAsync), the Automatic Service's own poll
/// cycle, and manual trigger-file processing (all in Worker.cs) - after ANY completed
/// run that covered a real range of invoice numbers, automatically computes the gap in
/// the EXACT range that run actually touched (everything in [lowest, highest] reference
/// number it saw that isn't already recorded as imported) and runs that gap through the
/// same InvoiceNumberList pipeline, recorded as its own separate History &amp; Logs entry.
///
/// Not a mode to pick by hand - added 2026-08-14 after confirming PortPro's list
/// endpoint can silently omit real (usually multi-charge-set) invoices under any
/// date or number-range query, so every range-based run gets this follow-up
/// automatically, regardless of source or original filter type.
/// </summary>
public static class GapFillRunner
{
    public static async Task RunIfApplicableAsync(
        SyncRequest originalRequest,
        SyncResult originalResult,
        SyncOrchestrator orchestrator,
        Action<SyncRequest> writeRequest,
        Action<string, SyncResult> writeResult,
        ILogger logger,
        CancellationToken ct)
    {
        // This IS a gap-fill's own sub-run (or a checkpoint of one) - never recurse.
        if (originalRequest.FilterType == FilterType.InvoiceNumberList) return;

        if (originalResult.Skipped || !originalResult.IsFinal)
        {
            logger.LogInformation(
                "Skipping gap-fill for {RequestId} - it didn't complete cleanly (Skipped={Skipped}, IsFinal={IsFinal}); " +
                "a future run covering the same ground will get its own gap-fill.",
                originalRequest.RequestId, originalResult.Skipped, originalResult.IsFinal);
            return;
        }

        var refs = originalResult.Outcomes
            .Select(o => o.ReferenceNumber)
            .Where(r => !string.IsNullOrEmpty(r))
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();
        if (refs.Count == 0)
        {
            logger.LogInformation("Skipping gap-fill for {RequestId} - nothing was fetched this run.", originalRequest.RequestId);
            return;
        }

        var min = refs[0];
        var max = refs[refs.Count - 1]; // net48 lacks the ^1 index-from-end operator

        // Prefix only - suffix (e.g. "-1") is a per-invoice marker, not a range-wide
        // pattern, and is ignored entirely for gap candidates (see SyncOrchestrator.
        // RunAsync's InvoiceNumberGapScan handling for why - a prior version required
        // min/max to share a suffix too and then applied it to the whole range).
        if (!ReferenceNumberFormat.TryParse(min, out var minPrefix, out _, out _, out _) ||
            !ReferenceNumberFormat.TryParse(max, out var maxPrefix, out _, out _, out _) ||
            !string.Equals(minPrefix, maxPrefix, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Skipping gap-fill for {RequestId} - could not determine a consistent prefix/number pattern from '{Min}'..'{Max}'.",
                originalRequest.RequestId, min, max);
            return;
        }

        var gapRequest = new SyncRequest
        {
            FilterType = FilterType.InvoiceNumberGapScan,
            StartInvoiceNumber = min,
            EndInvoiceNumber = max,
            RequestedBy = $"{originalRequest.RequestedBy}-gapfill"
        };

        logger.LogInformation(
            "Gap-fill for {OriginalRequestId}: sweeping {Start}..{End} (the range it actually touched) for anything the list-endpoint query may have missed.",
            originalRequest.RequestId, min, max);

        writeRequest(gapRequest);
        var gapResult = await orchestrator.RunAsync(gapRequest, ct,
            onProgress: partial => writeResult(gapRequest.RequestId, partial));
        writeResult(gapRequest.RequestId, gapResult);
    }
}
