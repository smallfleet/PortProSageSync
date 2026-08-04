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

        await _orchestrator.RunAsync(request, ct);
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
