using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PortProSage.Admin.Models;

namespace PortProSage.Admin.Services;

public class RunHistoryEntry
{
    public string RequestId { get; set; } = "";
    public SyncRequest? Request { get; set; }
    public SyncResult? Result { get; set; }
    public bool IsPending { get; set; }

    /// <summary>True for a run started via Manual Run (--run-once, its own dedicated
    /// process) - as opposed to a file-drop trigger picked up by the Automatic Service,
    /// or the Automatic Service's own periodic poll.</summary>
    public bool IsManual { get; set; }

    /// <summary>True for one cycle of the Automatic Service's own periodic poll
    /// (Worker.RunAutomaticLastChangedSyncAsync) - which now writes a request/result
    /// file pair the same way Manual Run does (see TriggerFileManager.WriteResult),
    /// so a crashed cycle (request written, no result) is detected identically to an
    /// orphaned Manual Run instead of needing separate log-reconstruction logic.</summary>
    public bool IsAutomaticPoll { get; set; }

    /// <summary>True when this entry has no on-disk request/result file at all and was
    /// reconstructed purely from "Starting sync .../Finished sync ..." log lines - a
    /// fallback for automatic-poll cycles that ran before Worker.cs started writing
    /// real files for them (see IsAutomaticPoll). New auto-poll runs no longer need
    /// this path.</summary>
    public bool ReconstructedFromLog { get; set; }

    /// <summary>Set by MainForm.RefreshHistoryList (WMI-based, needs live process
    /// state - not something this static service can determine on its own) for a
    /// pending manual entry whose *.request.json command line matches a currently-
    /// running PortProSage.Service.exe --run-once process. False for a pending entry
    /// means the process that was supposed to handle it is gone without ever writing
    /// a result - crashed, was force-killed, or hit a fatal condition like
    /// Sage50Client.TerminateOnFatalWriteError's Environment.Exit.</summary>
    public bool IsLiveProcess { get; set; }

    /// <summary>Set by MainForm.RefreshHistoryList for a pending, non-live manual
    /// entry - the timestamp of the last log line found in this entry's window, used
    /// as a stand-in "Finished" time when no result.json exists to supply a real one.
    /// Null until computed; also null if the log has nothing for this window.</summary>
    public DateTimeOffset? LastLogActivityUtc { get; set; }

    public DateTimeOffset SortKey => Result?.FinishedAtUtc ?? Request?.RequestedAtUtc ?? DateTimeOffset.MinValue;
}

public static class RunHistoryService
{
    private static readonly Regex StartLineRegex = new(
        @"^(?<ts>\S+ \S+ \S+) \[INF\] Starting sync (?<id>\S+) \((?<filter>[^,]+), UseWatermark=(?<watermark>\w+)",
        RegexOptions.Compiled);

    private static readonly Regex FinishLineRegex = new(
        @"^(?<ts>\S+ \S+ \S+) \[INF\] Finished sync (?<id>\S+): fetched=(?<fetched>\d+) imported=(?<imported>\d+) alreadyImported=(?<already>\d+) zeroAmount=(?<zero>\d+) failedValidation=(?<failedval>\d+) failedImport=(?<failedimp>\d+)",
        RegexOptions.Compiled);

    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff zzz";

    public static List<RunHistoryEntry> ListRuns(string triggerFolder, string processedFolder, string logFolder, string manualRunFolder, string autoPollFolder)
    {
        var byId = new Dictionary<string, RunHistoryEntry>();

        if (Directory.Exists(processedFolder))
        {
            foreach (var resultFile in Directory.EnumerateFiles(processedFolder, "*.result.json"))
            {
                var id = Path.GetFileName(resultFile).Replace(".result.json", "");
                var result = TryDeserialize<SyncResult>(resultFile);
                var requestFile = Path.Combine(processedFolder, $"{id}.request.json");
                var request = File.Exists(requestFile) ? TryDeserialize<SyncRequest>(requestFile) : null;
                byId[id] = new RunHistoryEntry { RequestId = id, Request = request, Result = result, IsPending = false };
            }
        }

        if (Directory.Exists(triggerFolder))
        {
            foreach (var requestFile in Directory.EnumerateFiles(triggerFolder, "*.request.json"))
            {
                var id = Path.GetFileName(requestFile).Replace(".request.json", "");
                if (byId.ContainsKey(id)) continue; // already processed and archived
                var request = TryDeserialize<SyncRequest>(requestFile);
                byId[id] = new RunHistoryEntry { RequestId = id, Request = request, Result = null, IsPending = true };
            }
        }

        // Manual Run's own folder (a subfolder of TriggerFolder the Automatic
        // Service's non-recursive scan never sees) - --run-once writes its
        // result.json right next to the request.json, no archiving/moving
        // involved, so both files just sit here permanently once done.
        if (Directory.Exists(manualRunFolder))
        {
            foreach (var requestFile in Directory.EnumerateFiles(manualRunFolder, "*.request.json"))
            {
                var id = Path.GetFileName(requestFile).Replace(".request.json", "");
                var request = TryDeserialize<SyncRequest>(requestFile);
                var resultFile = Path.Combine(manualRunFolder, $"{id}.result.json");
                var result = File.Exists(resultFile) ? TryDeserialize<SyncResult>(resultFile) : null;
                byId[id] = new RunHistoryEntry { RequestId = id, Request = request, Result = result, IsPending = result is null || !result.IsFinal, IsManual = true };
            }
        }

        // The Automatic Service's own poll cycles - same on-disk shape as Manual
        // Run's folder (request written before the run, result written after, no
        // archiving/moving), so a crashed cycle (request with no result) is exactly
        // as detectable as an orphaned Manual Run.
        if (Directory.Exists(autoPollFolder))
        {
            foreach (var requestFile in Directory.EnumerateFiles(autoPollFolder, "*.request.json"))
            {
                var id = Path.GetFileName(requestFile).Replace(".request.json", "");
                var request = TryDeserialize<SyncRequest>(requestFile);
                var resultFile = Path.Combine(autoPollFolder, $"{id}.result.json");
                var result = File.Exists(resultFile) ? TryDeserialize<SyncResult>(resultFile) : null;
                byId[id] = new RunHistoryEntry { RequestId = id, Request = request, Result = result, IsPending = result is null || !result.IsFinal, IsAutomaticPoll = true };
            }
        }

        // Fallback only - covers automatic-poll cycles that ran before Worker.cs
        // started writing real request/result files for them (see IsAutomaticPoll),
        // reconstructed from "Starting sync .../Finished sync ..." log lines. Skips
        // anything already found as a real file above via knownIds.
        foreach (var entry in ReconstructFromLogs(logFolder, byId.Keys))
        {
            byId[entry.RequestId] = entry;
        }

        return byId.Values.OrderByDescending(e => e.SortKey).ToList();
    }

    private static IEnumerable<RunHistoryEntry> ReconstructFromLogs(string logFolder, ICollection<string> knownIds)
    {
        if (!Directory.Exists(logFolder)) yield break;

        var starts = new Dictionary<string, (DateTimeOffset Timestamp, string FilterType, bool UseWatermark)>();
        var finishedIds = new HashSet<string>();

        foreach (var day in new[] { DateTimeOffset.Now, DateTimeOffset.Now.AddDays(-1) })
        {
            var path = Path.Combine(logFolder, $"portpro-sage-sync-{day:yyyyMMdd}.log");
            if (!File.Exists(path)) continue;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                var startMatch = StartLineRegex.Match(line);
                if (startMatch.Success)
                {
                    var id = startMatch.Groups["id"].Value;
                    if (DateTimeOffset.TryParseExact(startMatch.Groups["ts"].Value, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var startTs))
                    {
                        starts[id] = (startTs, startMatch.Groups["filter"].Value.Trim('"'),
                            string.Equals(startMatch.Groups["watermark"].Value, "true", StringComparison.OrdinalIgnoreCase));
                    }
                    continue;
                }

                var finishMatch = FinishLineRegex.Match(line);
                if (!finishMatch.Success) continue;

                var finishId = finishMatch.Groups["id"].Value;
                finishedIds.Add(finishId);
                if (knownIds.Contains(finishId)) continue; // already have a real result.json for this one

                if (!DateTimeOffset.TryParseExact(finishMatch.Groups["ts"].Value, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var finishTs))
                {
                    continue;
                }

                yield return new RunHistoryEntry
                {
                    RequestId = finishId,
                    ReconstructedFromLog = true,
                    Result = new SyncResult
                    {
                        RequestId = finishId,
                        StartedAtUtc = starts.TryGetValue(finishId, out var s) ? s.Timestamp : finishTs,
                        FinishedAtUtc = finishTs,
                        // A matched "Finished sync" line IS the run's own completion marker -
                        // this was never set here, so every log-reconstructed entry looked
                        // permanently "interrupted" to any code checking IsFinal (see
                        // MainForm.RunTab.cs's RefreshPreviousRunSection) even though it
                        // genuinely completed.
                        IsFinal = true,
                        InvoicesFetched = int.Parse(finishMatch.Groups["fetched"].Value),
                        InvoicesImported = int.Parse(finishMatch.Groups["imported"].Value),
                        InvoicesSkippedAlreadyImported = int.Parse(finishMatch.Groups["already"].Value),
                        InvoicesSkippedZeroOrNegativeAmount = int.Parse(finishMatch.Groups["zero"].Value),
                        InvoicesFailedValidation = int.Parse(finishMatch.Groups["failedval"].Value),
                        InvoicesFailedImport = int.Parse(finishMatch.Groups["failedimp"].Value)
                    }
                };
            }
        }

        // A "Starting sync" with no matching "Finished sync" anywhere in either
        // day's log - the automatic poll started this cycle and the process died
        // before it could finish (crashed, or hit a fatal condition like
        // Sage50Client.TerminateOnFatalWriteError's Environment.Exit). Confirmed
        // live 2026-08-09 this made the run completely invisible in History &
        // Logs, since previously only a matched start/finish pair produced an
        // entry at all - a start with no finish was silently dropped. Yielded
        // here as a pending entry (no Result) so MainForm.RefreshHistoryList's
        // existing "interrupted, synthesize Finished from the log" handling
        // picks it up the same way an orphaned Manual Run already does.
        foreach (var (id, info) in starts)
        {
            if (finishedIds.Contains(id) || knownIds.Contains(id)) continue;

            yield return new RunHistoryEntry
            {
                RequestId = id,
                ReconstructedFromLog = true,
                IsPending = true,
                Request = new SyncRequest
                {
                    RequestId = id,
                    FilterType = Enum.TryParse<FilterType>(info.FilterType, out var filterType) ? filterType : FilterType.LastChangedDate,
                    UseWatermark = info.UseWatermark,
                    RequestedAtUtc = info.Timestamp,
                    RequestedBy = "automatic poll"
                }
            };
        }
    }

    private static T? TryDeserialize<T>(string path)
    {
        try
        {
            // FileShare.ReadWrite, not File.ReadAllText's default (FileShare.Read) - this
            // is called from ListRuns, itself called every 2 seconds by ResultPollTimer_Tick
            // while a Manual Run may be actively checkpointing the SAME result.json this
            // reads - a second occurrence of the exact collision fixed in TriggerService.
            // TryReadResult (see its doc comment), missed there since this is a separate
            // read path scanning the whole history, not just the one pending request.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return JsonSerializer.Deserialize<T>(reader.ReadToEnd());
        }
        catch
        {
            return default;
        }
    }
}
