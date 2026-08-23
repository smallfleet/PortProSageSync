using System.Text.Json;

namespace PortProSage.Admin.Services;

/// <summary>Backs History &amp; Logs' Delete feature (MainForm.HistoryTab.cs) - removes
/// everything a run actually produced, not just the row from the grid: its own
/// request/result JSON files, any failed-transaction CSV report it generated, and
/// the imported_invoice tracking rows it created (so a deleted run's invoices are
/// treated as brand new again on the next real run, matching what deleting its
/// history record implies). Never touches the shared daily log file itself - other
/// unrelated runs' lines are interleaved in the same file, so surgically removing
/// just this run's lines isn't attempted; a ReconstructedFromLog entry (or any
/// entry whose log lines fall within RunHistoryService's 2-day reconstruction
/// window) is instead permanently excluded via a small "deleted ids" list, so it
/// doesn't reappear.</summary>
public static class RunDeletionService
{
    private const string DeletedIdsFileName = "deleted-history-ids.json";

    public class DeleteResult
    {
        public int FilesDeleted;
        public int FailedTransactionReportsDeleted;
        public int ImportedInvoiceRowsDeleted;
    }

    public static DeleteResult DeleteEntries(
        string triggerFolder,
        string failedTransactionsFolder,
        string stateDatabasePath,
        string logFolder,
        IEnumerable<RunHistoryEntry> entries)
    {
        var result = new DeleteResult();
        var deletedIds = LoadDeletedIds(triggerFolder);
        // Grouped by the run's own Sage50Path (not one flat list) - a reference
        // number is only unique WITHIN a Sage 50 path now (see
        // ImportedInvoiceStateService.DeleteByReferenceNumbers), and a single
        // batch of selected rows can span runs against different paths. The ""
        // key covers runs recorded before path-tracking existed, or reconstructed
        // from a log with no Result at all - those still delete unscoped, exactly
        // as every entry did before this feature existed (empty string is never a
        // real Sage50Path value, so it can't collide with one).
        var referenceNumbersByPath = new Dictionary<string, List<string>>();

        foreach (var entry in entries)
        {
            if (TryDeleteFile(entry.RequestFilePath)) result.FilesDeleted++;
            if (TryDeleteFile(entry.ResultFilePath)) result.FilesDeleted++;

            var refs = GetImportedReferenceNumbers(entry, logFolder);
            var path = entry.Result?.Sage50Path ?? string.Empty;
            if (!referenceNumbersByPath.TryGetValue(path, out var list))
            {
                list = new List<string>();
                referenceNumbersByPath[path] = list;
            }
            list.AddRange(refs);

            if (!string.IsNullOrWhiteSpace(failedTransactionsFolder))
            {
                result.FailedTransactionReportsDeleted += DeleteMatchingFailedTransactionReports(failedTransactionsFolder, entry.RequestId);
            }

            deletedIds.Add(entry.RequestId);
        }

        SaveDeletedIds(triggerFolder, deletedIds);

        foreach (var (path, referenceNumbers) in referenceNumbersByPath)
        {
            if (referenceNumbers.Count == 0) continue;
            result.ImportedInvoiceRowsDeleted += ImportedInvoiceStateService.DeleteByReferenceNumbers(
                stateDatabasePath, referenceNumbers, path.Length == 0 ? null : path);
        }

        return result;
    }

    public static HashSet<string> LoadDeletedIds(string triggerFolder)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(triggerFolder)) return ids;

        var path = Path.Combine(triggerFolder, DeletedIdsFileName);
        if (!File.Exists(path)) return ids;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var array = JsonSerializer.Deserialize<List<string>>(reader.ReadToEnd());
            if (array is not null) ids = new HashSet<string>(array, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            // Corrupt/unreadable - start fresh rather than block History & Logs entirely.
        }

        return ids;
    }

    private static void SaveDeletedIds(string triggerFolder, HashSet<string> ids)
    {
        if (string.IsNullOrWhiteSpace(triggerFolder)) return;

        try
        {
            Directory.CreateDirectory(triggerFolder);
            var path = Path.Combine(triggerFolder, DeletedIdsFileName);
            File.WriteAllText(path, JsonSerializer.Serialize(ids.ToList(), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best-effort - if this fails to save, the deleted row(s) may reappear
            // on the next refresh (only for entries whose log lines are still
            // within the 2-day reconstruction window), but the files/DB rows this
            // call already removed stay removed either way.
        }
    }

    /// <summary>Reference numbers this run actually, newly imported - real Outcomes
    /// when available (excludes ALREADY_IMPORTED/DRYRUN entries, which never wrote
    /// a real Sage 50 invoice), falling back to the log's own TRANSFER lines (which
    /// by construction only cover genuine new imports - see SyncOrchestrator.
    /// RunAsync) for entries with no Outcomes recorded, e.g. automatic-poll runs.</summary>
    private static IEnumerable<string> GetImportedReferenceNumbers(RunHistoryEntry entry, string logFolder)
    {
        if (entry.Result?.Outcomes is { Count: > 0 } outcomes)
        {
            return outcomes
                .Where(o => o.Success && o.Sage50InvoiceNumber is not null &&
                            o.Sage50InvoiceNumber != "ALREADY_IMPORTED" &&
                            !o.Sage50InvoiceNumber.StartsWith("DRYRUN-", StringComparison.Ordinal))
                .Select(o => o.ReferenceNumber)
                .Where(r => !string.IsNullOrEmpty(r));
        }

        if (entry.Result is null || string.IsNullOrWhiteSpace(logFolder)) return Enumerable.Empty<string>();

        var logLines = LogExtractorService.ExtractForWindow(logFolder, entry.Result.StartedAtUtc, entry.Result.FinishedAtUtc);
        return LogExtractorService.ExtractTransferredInvoices(logLines)
            .Select(t => t.PortProReference)
            .Where(r => !string.IsNullOrEmpty(r))
            .ToList();
    }

    /// <summary>Failed-transaction CSVs are named by timestamp, not RequestId (see
    /// FailedTransactionReport.WriteIfAnyFailures), but every row in one file always
    /// belongs to the same single run - so matching on the RequestId appearing
    /// anywhere in the file reliably identifies "this run's report" without needing
    /// to parse the CSV.</summary>
    private static int DeleteMatchingFailedTransactionReports(string failedTransactionsFolder, string requestId)
    {
        if (!Directory.Exists(failedTransactionsFolder)) return 0;

        var deleted = 0;
        foreach (var path in Directory.EnumerateFiles(failedTransactionsFolder, "failed-transactions-*.csv"))
        {
            try
            {
                var lines = File.ReadLines(path);
                var matches = lines.Skip(1).Any(line => line.Contains(requestId, StringComparison.OrdinalIgnoreCase));
                if (matches && TryDeleteFile(path)) deleted++;
            }
            catch
            {
                // Locked/unreadable - skip rather than fail the whole batch.
            }
        }

        return deleted;
    }

    private static bool TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;

        try
        {
            File.Delete(path);
            return true;
        }
        catch
        {
            return false; // locked/in-use - best-effort, doesn't block the rest of the batch
        }
    }
}
