using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PortProSage.Core.Config;

namespace PortProSage.Core.Sync;

/// <summary>
/// Deletes daily rolling log files (Sync.LogFolder, Serilog's
/// "portpro-sage-sync-yyyyMMdd.log" naming) older than SyncSettings.
/// LogRetentionDays. Called once at the end of every SyncOrchestrator.RunAsync
/// (not on a separate timer) so cleanup is tied to actual activity rather than
/// needing its own scheduling - a quiet system just doesn't accumulate logs to
/// clean up in the first place.
///
/// This is destructive and NOT recoverable - a deleted log file is gone. Guarded
/// by LogRetentionDays defaulting to 0 (disabled) both in code and in a fresh
/// appsettings.json, so cleanup only ever runs if a deployment has explicitly
/// opted in with a positive day count.
/// </summary>
public static class LogRetentionService
{
    private static readonly Regex LogFileNamePattern = new(
        @"^portpro-sage-sync-(?<date>\d{8})\.log$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static void CleanupOldLogs(SyncSettings settings, ILogger logger)
    {
        if (settings.LogRetentionDays <= 0)
        {
            return; // 0 (or negative) means cleanup is disabled - never delete anything.
        }

        if (!Directory.Exists(settings.LogFolder))
        {
            return;
        }

        var cutoff = DateTime.Today.AddDays(-settings.LogRetentionDays);
        var deleted = new List<string>();

        foreach (var path in Directory.EnumerateFiles(settings.LogFolder, "portpro-sage-sync-*.log"))
        {
            var fileName = Path.GetFileName(path);
            var match = LogFileNamePattern.Match(fileName);
            if (!match.Success)
            {
                continue; // doesn't match the expected naming - leave it alone rather than guess
            }

            if (!DateTime.TryParseExact(match.Groups["date"].Value, "yyyyMMdd",
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var fileDate))
            {
                continue;
            }

            if (fileDate >= cutoff)
            {
                continue; // within the retention window - keep it
            }

            try
            {
                File.Delete(path);
                deleted.Add(fileName);
            }
            catch (Exception ex)
            {
                // Most likely today's file still open for writing, or a permissions
                // issue - skip it and keep going; it'll be retried on the next run.
                logger.LogWarning(ex, "Could not delete old log file {FileName} during log retention cleanup - will retry on a later run.", fileName);
            }
        }

        if (deleted.Count > 0)
        {
            logger.LogWarning(
                "Log retention cleanup: permanently deleted {Count} log file(s) older than {RetentionDays} day(s) (cutoff {Cutoff:yyyy-MM-dd}): {Files}",
                deleted.Count, settings.LogRetentionDays, cutoff, string.Join(", ", deleted));
        }
    }
}
