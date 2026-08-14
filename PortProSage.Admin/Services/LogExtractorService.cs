using System.Globalization;
using System.Text.RegularExpressions;

namespace PortProSage.Admin.Services;

/// <summary>One row parsed from a "TRANSFER: ..." log line (see SyncOrchestrator.RunAsync
/// in Core) - one per invoice actually posted to Sage 50 this run.</summary>
public class TransferredInvoiceRow
{
    public string PortProReference { get; set; } = string.Empty;
    public string Sage50InvoiceNumber { get; set; } = string.Empty;
    public string PortProDate { get; set; } = string.Empty;
    public string Sage50Date { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public decimal TaxCharged { get; set; }
}

/// <summary>One row parsed from an "OUTCOME: ..." log line (see SyncOrchestrator.RunAsync
/// in Core) - every invoice processed this run, success or failure alike. Used as a
/// fallback source for the Admin app's Per-invoice Outcomes tab when result.json comes
/// back null/empty (see MainForm.HistoryTab.cs's ShowSelectedHistoryEntry) - the log line
/// is written live, per invoice, so it survives even when the checkpoint file itself
/// doesn't (e.g. a disk-full write corrupting/losing the whole file).</summary>
public class LoggedOutcomeRow
{
    public string ReferenceNumber { get; set; } = string.Empty;
    public string PortProDate { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string Sage50InvoiceNumber { get; set; } = string.Empty;
    public string Messages { get; set; } = string.Empty;
}

/// <summary>
/// Pulls just the log lines belonging to one execution out of the Service's
/// rolling daily log file (Serilog: "portpro-sage-sync-yyyyMMdd.log", one line
/// per event, leading timestamp "yyyy-MM-dd HH:mm:ss.fff zzz"). Works by time
/// window (StartedAtUtc..FinishedAtUtc from that run's SyncResult) rather than
/// by matching RequestId text in every line, because not every line mentions
/// the RequestId (e.g. per-HTTP-request or per-SDK-call detail lines don't) -
/// the time window is complete and exact instead. This is safe because the
/// Worker's loop is single-threaded and processes one request fully before
/// starting the next, so two different runs' log windows never overlap.
/// </summary>
public static class LogExtractorService
{
    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff zzz";

    // Mirrors the exact structured-logging call in SyncOrchestrator.RunAsync -
    // "TRANSFER: Ref=RSRE_000123 Sage50Number=1045 PortProDate=2026-08-01 Sage50Date=2026-08-01 TotalAmount=450.00 TaxCharged=58.50".
    private static readonly Regex TransferLinePattern = new(
        @"TRANSFER: Ref=(?<ref>\S+) Sage50Number=(?<sage>\S+) PortProDate=(?<pdate>\S+) Sage50Date=(?<sdate>\S+) TotalAmount=(?<total>-?[\d.]+) TaxCharged=(?<tax>-?[\d.]+)",
        RegexOptions.Compiled);

    // Mirrors the exact structured-logging call in SyncOrchestrator.RunAsync -
    // "OUTCOME: Ref=RSRE_000823 PortProDate=2026-08-01 Success=False Sage50Number=(none) Messages=[IMPORT ERROR: ...]".
    // Messages is captured greedily to the LAST "]" on the line, not the first, since
    // the message text itself can legitimately contain "]" (e.g. an exception message).
    private static readonly Regex OutcomeLinePattern = new(
        @"OUTCOME: Ref=(?<ref>\S+) PortProDate=(?<pdate>\S+) Success=(?<success>True|False) Sage50Number=(?<sage>\S+) Messages=\[(?<messages>.*)\]\s*$",
        RegexOptions.Compiled);

    /// <summary>Parses "Invoice Transferred" rows out of an already-extracted set of log
    /// lines for one run (see ExtractForWindow) - built from the log, not from
    /// result.json's Outcomes, so it works identically for automatic-poll runs (which
    /// never write a result.json) and manual/trigger runs alike.</summary>
    public static List<TransferredInvoiceRow> ExtractTransferredInvoices(IEnumerable<string> logLines)
    {
        var rows = new List<TransferredInvoiceRow>();
        foreach (var line in logLines)
        {
            var match = TransferLinePattern.Match(line);
            if (!match.Success) continue;

            rows.Add(new TransferredInvoiceRow
            {
                PortProReference = match.Groups["ref"].Value,
                Sage50InvoiceNumber = match.Groups["sage"].Value,
                PortProDate = match.Groups["pdate"].Value,
                Sage50Date = match.Groups["sdate"].Value,
                TotalAmount = decimal.TryParse(match.Groups["total"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var total) ? total : 0m,
                TaxCharged = decimal.TryParse(match.Groups["tax"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var tax) ? tax : 0m
            });
        }
        return rows;
    }

    /// <summary>Parses "Per-invoice outcomes" rows out of an already-extracted set of log
    /// lines for one run - the log-based fallback used when result.json's own Outcomes
    /// list comes back null or empty (see this class's LoggedOutcomeRow doc comment for
    /// why that can happen even for a run that mostly succeeded).</summary>
    public static List<LoggedOutcomeRow> ExtractOutcomes(IEnumerable<string> logLines)
    {
        var rows = new List<LoggedOutcomeRow>();
        foreach (var line in logLines)
        {
            var match = OutcomeLinePattern.Match(line);
            if (!match.Success) continue;

            rows.Add(new LoggedOutcomeRow
            {
                ReferenceNumber = match.Groups["ref"].Value,
                PortProDate = match.Groups["pdate"].Value,
                Success = string.Equals(match.Groups["success"].Value, "True", StringComparison.OrdinalIgnoreCase),
                Sage50InvoiceNumber = match.Groups["sage"].Value,
                Messages = match.Groups["messages"].Value
            });
        }
        return rows;
    }

    public static List<string> ExtractForWindow(string logFolder, DateTimeOffset startedAtUtc, DateTimeOffset finishedAtUtc)
    {
        var result = new List<string>();
        if (!Directory.Exists(logFolder)) return result;

        // A run that starts just before midnight local time could finish just
        // after - check every daily file whose date falls within the window
        // (plus one day of slack on each side to be safe about local-vs-UTC
        // date boundaries), not just a single guessed file name.
        var localStart = startedAtUtc.ToLocalTime().Date.AddDays(-1);
        var localEnd = finishedAtUtc.ToLocalTime().Date.AddDays(1);

        for (var day = localStart; day <= localEnd; day = day.AddDays(1))
        {
            var path = Path.Combine(logFolder, $"portpro-sage-sync-{day:yyyyMMdd}.log");
            if (!File.Exists(path)) continue;

            foreach (var line in ReadLinesSafely(path))
            {
                if (TryGetLineTimestamp(line, out var ts) && ts >= startedAtUtc.AddSeconds(-1) && ts <= finishedAtUtc.AddSeconds(1))
                {
                    result.Add(line);
                }
                else if (result.Count > 0 && !TryGetLineTimestamp(line, out _))
                {
                    // A continuation line (e.g. a multi-line exception stack trace)
                    // has no leading timestamp of its own - keep it attached to
                    // whichever timestamped line came before it, if that line was
                    // already included.
                    result.Add(line);
                }
            }
        }

        return result;
    }

    /// <summary>The timestamp of the last timestamped line in an already-extracted set
    /// of log lines (see ExtractForWindow) - used as a stand-in "Finished" time for a
    /// run that never got a result.json, since its own log activity is the only clue
    /// left as to when it actually stopped doing anything.</summary>
    public static DateTimeOffset? GetLastTimestamp(IEnumerable<string> lines)
    {
        DateTimeOffset? last = null;
        foreach (var line in lines)
        {
            if (TryGetLineTimestamp(line, out var ts)) last = ts;
        }
        return last;
    }

    /// <summary>File.ReadAllLines while the Service may still be writing to the same file.</summary>
    private static IEnumerable<string> ReadLinesSafely(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            yield return line;
        }
    }

    private static bool TryGetLineTimestamp(string line, out DateTimeOffset timestamp)
    {
        // Leading segment before " [" holds the timestamp, e.g.
        // "2026-08-04 16:52:36.139 -04:00 [INF] Message here".
        var bracketIndex = line.IndexOf(" [", StringComparison.Ordinal);
        var candidate = bracketIndex > 0 ? line[..bracketIndex] : line;
        return DateTimeOffset.TryParseExact(candidate, TimestampFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out timestamp);
    }
}
