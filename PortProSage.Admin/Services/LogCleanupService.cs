using System.Text.RegularExpressions;

namespace PortProSage.Admin.Services;

/// <summary>Deletes daily rolling log files ("portpro-sage-sync-yyyyMMdd.log")
/// older than the given retention days - an Admin-side mirror of
/// PortProSage.Core.Sync.LogRetentionService (Admin can't reference Core;
/// different target frameworks: net10.0-windows here vs net48 there). That
/// Core version only ever runs automatically, once, at the end of a real sync
/// run; this exists so the same cleanup can also be triggered on demand
/// ("Apply Now" on the Settings tab, or as part of clearing imported-invoice
/// tracking) without waiting for a run. This is destructive and NOT
/// recoverable - a deleted log file is gone.</summary>
public static class LogCleanupService
{
    private static readonly Regex LogFileNamePattern = new(
        @"^portpro-sage-sync-(?<date>\d{8})\.log$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Returns the file names actually deleted. 0/negative retentionDays
    /// or a missing folder is treated as "nothing to do", not an error.</summary>
    public static List<string> CleanupOldLogs(string logFolder, int retentionDays)
    {
        var deleted = new List<string>();
        if (retentionDays <= 0 || string.IsNullOrWhiteSpace(logFolder) || !Directory.Exists(logFolder))
        {
            return deleted;
        }

        var cutoff = DateTime.Today.AddDays(-retentionDays);

        foreach (var path in Directory.EnumerateFiles(logFolder, "portpro-sage-sync-*.log"))
        {
            var fileName = Path.GetFileName(path);
            var match = LogFileNamePattern.Match(fileName);
            if (!match.Success) continue;

            if (!DateTime.TryParseExact(match.Groups["date"].Value, "yyyyMMdd",
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var fileDate))
            {
                continue;
            }

            if (fileDate >= cutoff) continue; // within the retention window - keep it

            try
            {
                File.Delete(path);
                deleted.Add(fileName);
            }
            catch
            {
                // Most likely today's file still open for writing, or a
                // permissions issue - skip it and keep going.
            }
        }

        return deleted;
    }
}
