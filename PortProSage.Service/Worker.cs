using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PortProSage.Core.Config;
using PortProSage.Core.Data;
using PortProSage.Core.Models;
using PortProSage.Core.Sync;

namespace PortProSage.Service;

public class Worker : BackgroundService
{
    private readonly SyncOrchestrator _orchestrator;
    private readonly SyncStateRepository _state;
    private readonly SyncSettings _syncSettings;
    private readonly ILogger<Worker> _logger;

    // Manual trigger files are checked far more often than the full PortPro poll,
    // since they represent an operator actively waiting on a result.
    private static readonly TimeSpan TriggerPollInterval = TimeSpan.FromSeconds(15);

    public Worker(SyncOrchestrator orchestrator, SyncStateRepository state, SyncSettings syncSettings, ILogger<Worker> logger)
    {
        _orchestrator = orchestrator;
        _state = state;
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
        var watermark = _state.GetLastChangedWatermark()
            ?? DateTimeOffset.UtcNow.AddDays(-Math.Max(1, _syncSettings.InitialLookbackDays));

        var request = new SyncRequest
        {
            FilterType = FilterType.LastChangedDate,
            From = watermark,
            To = DateTimeOffset.UtcNow,
            RequestedBy = "auto-poll"
        };

        _logger.LogInformation("Running automatic sync for invoices changed since {Watermark}", watermark);
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
