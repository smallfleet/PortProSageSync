using PortProSage.Core.Models;

namespace PortProSage.Core.Sync;

/// <summary>
/// Writes a CSV listing every failed outcome from one sync run, named with a
/// microsecond-precision timestamp so rapid/concurrent runs never collide. See
/// AppSettings.Email and SyncSettings.FailedTransactionsFolder, and
/// SyncOrchestrator (which calls this and, if any failures exist, emails the result).
/// </summary>
public static class FailedTransactionReport
{
    /// <summary>
    /// Writes the CSV and returns its full path, or null if there were no failed
    /// outcomes to report (callers shouldn't email an empty report).
    /// </summary>
    public static string? WriteIfAnyFailures(SyncResult result, string folder)
    {
        var failures = result.Outcomes.Where(o => !o.Success).ToList();
        if (failures.Count == 0)
        {
            return null;
        }

        Directory.CreateDirectory(folder);

        // "ffffff" = 6 fractional-second digits = microsecond precision.
        var timestamp = DateTimeOffset.UtcNow;
        var fileName = $"failed-transactions-{timestamp:yyyyMMdd-HHmmss-ffffff}.csv";
        var path = Path.Combine(folder, fileName);

        using var writer = new StreamWriter(path, append: false, System.Text.Encoding.UTF8);
        writer.WriteLine("TimestampUtc,RequestId,PortProInvoiceId,ReferenceNumber,Messages");

        foreach (var outcome in failures)
        {
            var messages = string.Join(" | ", outcome.Messages);
            writer.WriteLine(string.Join(",",
                CsvField(timestamp.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ")),
                CsvField(result.RequestId),
                CsvField(outcome.PortProInvoiceId),
                CsvField(outcome.ReferenceNumber),
                CsvField(messages)));
        }

        return path;
    }

    private static string CsvField(string value)
    {
        if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
