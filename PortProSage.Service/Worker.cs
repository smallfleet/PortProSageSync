using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PortProSage.Core.Config;
using PortProSage.Core.Models;
using PortProSage.Core.Sync;

namespace PortProSage.Service;

public class Worker : BackgroundService
{
    private readonly SyncOrchestrator _orchestrator;
    private readonly SyncSettings _syncSettings;
    private readonly ILogger<Worker> _logger;

    // Manual trigger files are checked far more often than the full PortPro poll,
    // since they represent an operator actively waiting on a result.
    private static readonly TimeSpan TriggerPollInterval = TimeSpan.FromSeconds(15);

    public Worker(SyncOrchestrator orchestrator, SyncSettings syncSettings, ILogger<Worker> logger)
    {
        _orchestrator = orchestrator;
        _syncSettings = syncSettings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(_syncSettings.TriggerFolder);
        Directory.CreateDirectory(_syncSettings.ProcessedTriggerFolder);

        var lastAutoPoll = DateTimeOffset.MinValue;
        var pollInterval = TimeSpan.FromMinutes(Math.Max(1, _syncSettings.PollingIntervalMinutes));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessManualTriggersAsync(stoppingToken);

                if (DateTimeOffset.UtcNow - lastAutoPoll >= pollInterval)
                {
                    await RunAutomaticLastChangedSyncAsync(stoppingToken);
                    lastAutoPoll = DateTimeOffset.UtcNow;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in worker loop - will retry after the poll interval.");
            }

            await Task.Delay(TriggerPollInterval, stoppingToken);
        }
    }

    private async Task RunAutomaticLastChangedSyncAsync(CancellationToken ct)
    {
        // From/To resolved inside SyncOrchestrator.RunAsync from the persisted
        // watermark - same "continue from where we left off" resolution a manual
        // trigger run with no --mode also uses, so there's one code path for it.
        var request = new SyncRequest
        {
            FilterType = FilterType.LastChangedDate,
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
        }
    }
}
