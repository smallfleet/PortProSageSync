using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PortProSage.Core.Config;
using PortProSage.Core.Models;
using PortProSage.Core.Sync;

namespace PortProSage.Service;

public class Worker : BackgroundService
{
    private readonly SyncOrchestrator _orchestrator;
    private readonly CustomerSyncService _customerSync;
    private readonly SyncSettings _syncSettings;
    private readonly ILogger<Worker> _logger;

    // Manual trigger files are checked far more often than the full PortPro poll,
    // since they represent an operator actively waiting on a result.
    private static readonly TimeSpan TriggerPollInterval = TimeSpan.FromSeconds(15);

    public Worker(SyncOrchestrator orchestrator, CustomerSyncService customerSync, SyncSettings syncSettings, ILogger<Worker> logger)
    {
        _orchestrator = orchestrator;
        _customerSync = customerSync;
        _syncSettings = syncSettings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(_syncSettings.TriggerFolder);
        Directory.CreateDirectory(_syncSettings.ProcessedTriggerFolder);

        // Replaces the old elapsed-interval poll (fire every N minutes) with a
        // fixed daily schedule driven off one absolute "next run" instant,
        // computed fresh from ScheduledRunHours: on startup, and again after
        // each run completes. ComputeNextScheduledRun always returns a time
        // strictly after "now", so even starting the Service in the middle of a
        // scheduled hour waits for that hour's NEXT occurrence rather than
        // firing immediately. Local time, matching how hours are displayed/
        // picked in the Admin app's Automatic Sync tab. Because the next run is
        // only ever computed AFTER the previous one finishes, a slow run that
        // crosses one or more scheduled hours is never "caught up" - it simply
        // resumes counting from whatever time it actually finished at, and the
        // sequential await here (no Task.Run/fire-and-forget) already
        // guarantees an automatic run and a manual trigger can never overlap.
        var nextScheduledRun = ComputeNextScheduledRun(DateTime.Now, _syncSettings.ScheduledRunHours);
        _logger.LogInformation("Next scheduled automatic sync: {NextRun}", nextScheduledRun);
        WriteAutomaticSyncStatus("Idle", nextScheduledRunLocal: nextScheduledRun);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Cheap existence check first - avoids flipping the status file to
                // "Running" and back for every single 15s tick when the trigger
                // folder is (as it usually is) empty; only genuinely busy ticks
                // report themselves as running.
                if (Directory.EnumerateFileSystemEntries(_syncSettings.TriggerFolder).Any())
                {
                    WriteAutomaticSyncStatus("Running", currentRunStartedLocal: DateTime.Now);
                    await ProcessManualTriggersAsync(stoppingToken);
                    WriteAutomaticSyncStatus("Idle", nextScheduledRunLocal: nextScheduledRun);
                }

                if (DateTime.Now >= nextScheduledRun)
                {
                    WriteAutomaticSyncStatus("Running", currentRunStartedLocal: DateTime.Now);
                    await RunAutomaticContinuousSyncAsync(stoppingToken);
                    nextScheduledRun = ComputeNextScheduledRun(DateTime.Now, _syncSettings.ScheduledRunHours);
                    _logger.LogInformation("Next scheduled automatic sync: {NextRun}", nextScheduledRun);
                    WriteAutomaticSyncStatus("Idle", nextScheduledRunLocal: nextScheduledRun);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in worker loop - will retry on the next check.");
            }

            await Task.Delay(TriggerPollInterval, stoppingToken);
        }
    }

    /// <summary>Small on-disk breadcrumb (next to state.db, so both this process and
    /// the Admin app can find it from the same already-known StateDatabasePath
    /// setting with no new config needed) letting the Admin app's status bar tell
    /// "actively processing right now" apart from "alive, but sleeping until the
    /// next scheduled time" - both look identical from the outside (the OS process
    /// is simply running) without this. Best-effort: a write failure only degrades
    /// the Admin app's display to its older, coarser "process is alive" text, never
    /// something worth stopping the actual sync over.</summary>
    private void WriteAutomaticSyncStatus(string state, DateTime? nextScheduledRunLocal = null, DateTime? currentRunStartedLocal = null)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(_syncSettings.StateDatabasePath));
            if (string.IsNullOrEmpty(dir)) return;
            var path = Path.Combine(dir, "automatic-sync-status.json");

            var json = JsonSerializer.Serialize(new
            {
                State = state,
                NextScheduledRunLocal = nextScheduledRunLocal?.ToString("o"),
                CurrentRunStartedLocal = currentRunStartedLocal?.ToString("o"),
                UpdatedAtLocal = DateTime.Now.ToString("o")
            });
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write automatic-sync-status.json - the Admin app's status display may fall back to a coarser \"running\" text.");
        }
    }

    /// <summary>The next absolute local DateTime, strictly after <paramref name="from"/>,
    /// at which one of scheduledHours occurs - today if an unpassed hour remains,
    /// otherwise the earliest hour tomorrow. Empty scheduledHours (no times
    /// selected in the Admin app yet) means automatic scheduling never fires -
    /// returns DateTime.MaxValue so the `>=` check in ExecuteAsync is never true,
    /// while manual/trigger-file requests keep being processed regardless.</summary>
    private static DateTime ComputeNextScheduledRun(DateTime from, List<int> scheduledHours)
    {
        if (scheduledHours.Count == 0) return DateTime.MaxValue;

        var sortedHours = scheduledHours.Distinct().OrderBy(h => h).ToList();
        foreach (var hour in sortedHours)
        {
            var candidate = from.Date.AddHours(hour);
            if (candidate > from) return candidate;
        }

        return from.Date.AddDays(1).AddHours(sortedHours[0]);
    }

    /// <summary>Pass-1 ("Continuous") of the Automatic Service's poll cycle -
    /// watermark-driven, based on the invoice's own creation/completed date
    /// (CompletedDateRange), NOT PortPro's "last changed" timestamp. Switched
    /// from LastChangedDate 2026-08-26 for consistency with Manual Run's own
    /// default "Invoice date" mode, and because there's no way yet to push an
    /// UPDATE to an already-posted Sage 50 invoice (Sage50Client only has
    /// CreateInvoiceAsync, no UpdateInvoiceAsync) - so catching an invoice
    /// purely because PortPro touched it again had nothing useful to do with
    /// that information anyway.
    ///
    /// Known, accepted limitation: an invoice edited in PortPro AFTER its own
    /// creation date will NOT be automatically re-surfaced once its creation
    /// date has scrolled past the current watermark - this pass only ever
    /// looks at creation date, never at what changed later. Catching that
    /// requires either a manual re-check (Manual Run covering that invoice's
    /// date/number again) or a future "Pass-3" (last-changed-date-driven, see
    /// USER_GUIDE.md's Automatic Sync section), deliberately not built yet -
    /// it would need a real Reverse+Insert capability (reverse the original
    /// posted invoice, then post a corrected one) to have anything useful to
    /// do once found, and that needs its own thorough design/testing, not
    /// bundled into this change.
    private async Task RunAutomaticContinuousSyncAsync(CancellationToken ct)
    {
        // From/To resolved inside SyncOrchestrator.RunAsync from the persisted
        // watermark - same "continue from where we left off" resolution a manual
        // trigger run with no --mode also uses, so there's one code path for it.
        var request = new SyncRequest
        {
            FilterType = FilterType.CompletedDateRange,
            UseWatermark = true,
            RequestedBy = "auto-poll"
        };

        var autoPollFolder = Path.Combine(_syncSettings.TriggerFolder, "auto-poll");

        // The Worker's own loop is single-threaded, so this specific cycle can never
        // overlap with an EARLIER automatic-poll cycle from this same process - but
        // nothing previously stopped a completely separate PortProSage.Service.exe
        // process (a Manual Run launched externally, or a second Service instance)
        // from opening Sage 50 at the same moment, which Sage 50 rejects as a second
        // simultaneous session. Check for that before attempting to connect, and
        // skip cleanly (logged, and recorded in history) rather than risk it.
        if (TryFindAnotherServiceProcess(out var otherPid))
        {
            var reason = $"Another PortProSage.Service.exe process (PID {otherPid}) was already running - skipped to avoid a second simultaneous Sage 50 session.";
            _logger.LogWarning("Skipping this automatic poll cycle: {Reason}", reason);

            var now = DateTimeOffset.UtcNow;
            var skipped = new SyncResult
            {
                RequestId = request.RequestId,
                StartedAtUtc = now,
                FinishedAtUtc = now,
                ProcessId = Process.GetCurrentProcess().Id,
                Skipped = true,
                SkipReason = reason
            };
            TriggerFileManager.Write(autoPollFolder, request);
            TriggerFileManager.WriteResult(autoPollFolder, request.RequestId, skipped);
            return;
        }

        // Request written BEFORE the run, result only AFTER (i.e. only if RunAsync
        // actually returns) - gives every automatic poll cycle the same on-disk
        // lifecycle as a Manual Run, so a cycle that dies mid-run (e.g. Sage50Client.
        // TerminateOnFatalWriteError's Environment.Exit, which never lets RunAsync
        // return at all) leaves a request-with-no-result behind instead of vanishing
        // completely - confirmed live 2026-08-09 that was happening: an automatic
        // poll that crashed produced no record of itself anywhere. See
        // TriggerFileManager.WriteResult's comment for the full reasoning.
        TriggerFileManager.Write(autoPollFolder, request);

        // Checkpointed after every invoice (onProgress), not just once at the very
        // end - confirmed live 2026-08-09 that a cycle killed/closed mid-run left
        // every count blank in History & Logs even though real progress had been
        // made, since nothing had ever been written. Each checkpoint overwrites the
        // same result file; the final write below (IsFinal=true) is just the last
        // of these overwrites if the run actually completes normally.
        var result = await _orchestrator.RunAsync(request, ct,
            onProgress: partial => TriggerFileManager.WriteResult(autoPollFolder, request.RequestId, partial));

        // Every automatic cycle gets its own gap-fill follow-up too - see
        // GapFillRunner's doc comment. Deliberately awaited here, inside this same
        // call, rather than fired off separately - the Worker's own loop is single-
        // threaded (ExecuteAsync only checks the poll interval again after this
        // whole method returns), so gap-filling taking a while naturally makes the
        // next scheduled cycle wait for it, with no separate concurrency guard needed.
        await GapFillRunner.RunIfApplicableAsync(request, result, _orchestrator,
            writeRequest: r => TriggerFileManager.Write(autoPollFolder, r),
            writeResult: (id, r) => TriggerFileManager.WriteResult(autoPollFolder, id, r),
            _logger, ct);

        // Once per automatic cycle - see CustomerSyncService's doc comment and
        // Sage50Settings.SyncCustomerUpdatesFromPortPro (the on/off switch).
        // Deliberately not skipped even when this cycle itself skipped/found
        // nothing (result.Skipped, or a caught-up watermark) - customer profile
        // changes are independent of whether any invoices happened to be due.
        //
        // Captured and folded into THIS cycle's own result, BEFORE the final
        // WriteResult below - mirrors the exact fix already made to Diagnostics.
        // RunOnceAsync (Manual Run's own equivalent path) for the same reason:
        // this call used to be fire-and-forget here, so a Sage 50 connection
        // failure during this trailing sweep left no trace anywhere in History &
        // Logs, only the raw log file, and the Updated count was never recorded
        // at all.
        var customerSyncResult = await _customerSync.SyncChangedCustomersAsync(ct);
        result.CustomersUpdated = customerSyncResult.Updated;
        if (customerSyncResult.FatalError is not null)
        {
            result.Outcomes.Add(new InvoiceProcessingOutcome
            {
                Success = false,
                Messages = { customerSyncResult.FatalError }
            });
        }

        TriggerFileManager.WriteResult(autoPollFolder, request.RequestId, result);
    }

    /// <summary>Any other live process running the exact same PortProSage.Service.exe
    /// binary (by path, not just image name, to avoid false positives from an
    /// unrelated same-named process elsewhere) - covers a Manual Run's dedicated
    /// --run-once process, a duplicate Service instance, or anything else that might
    /// be about to (or already) hold a Sage 50 connection.</summary>
    private static bool TryFindAnotherServiceProcess(out int otherPid)
    {
        otherPid = 0;
        var currentPid = Process.GetCurrentProcess().Id;
        string? currentPath;
        try
        {
            currentPath = Process.GetCurrentProcess().MainModule?.FileName;
        }
        catch
        {
            return false; // can't determine our own path - don't block on an inconclusive check
        }
        if (string.IsNullOrEmpty(currentPath)) return false;

        foreach (var p in Process.GetProcessesByName("PortProSage.Service"))
        {
            try
            {
                if (p.Id == currentPid) continue;
                if (string.Equals(p.MainModule?.FileName, currentPath, StringComparison.OrdinalIgnoreCase))
                {
                    otherPid = p.Id;
                    return true;
                }
            }
            catch
            {
                // access denied reading another user's process module info, or it
                // exited between enumeration and inspection - not a match.
            }
            finally
            {
                p.Dispose();
            }
        }
        return false;
    }

    private async Task ProcessManualTriggersAsync(CancellationToken ct)
    {
        foreach (var (path, request) in TriggerFileManager.ReadPending(_syncSettings.TriggerFolder))
        {
            _logger.LogInformation("Processing manual trigger request {RequestId} from {Path}", request.RequestId, path);

            var result = await _orchestrator.RunAsync(request, ct);
            TriggerFileManager.Archive(path, _syncSettings.ProcessedTriggerFolder, result);

            // Every trigger-file-driven run gets its own gap-fill follow-up too -
            // see GapFillRunner's doc comment. Written straight into
            // ProcessedTriggerFolder (not TriggerFolder) since it's already decided
            // and run immediately, never "pending" the way a dropped file is.
            await GapFillRunner.RunIfApplicableAsync(request, result, _orchestrator,
                writeRequest: r => TriggerFileManager.Write(_syncSettings.ProcessedTriggerFolder, r),
                writeResult: (id, r) => TriggerFileManager.WriteResult(_syncSettings.ProcessedTriggerFolder, id, r),
                _logger, ct);
        }
    }
}
