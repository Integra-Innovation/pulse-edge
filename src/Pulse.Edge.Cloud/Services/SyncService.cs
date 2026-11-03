using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Pulse.Edge.Storage.Services;

namespace Pulse.Edge.Cloud.Services;

public class SyncService
{
    private readonly ILogger<SyncService> _logger;
    private readonly QueueStorageService _storageService;
    private readonly CloudClient _cloudClient;

    public SyncService(
        ILogger<SyncService> logger,
        QueueStorageService storageService,
        CloudClient cloudClient)
    {
        _logger = logger;
        _storageService = storageService;
        _cloudClient = cloudClient;
    }

    // Runs indefinitely in the background to sync SQLite records to PULSE Cloud
    public async Task StartSyncLoopAsync(string deviceId, string apiKey, CancellationToken stoppingToken)
    {
        _logger.LogInformation("PULSE Cloud Sync loop started.");

        bool isSyncCurrentlyPaused = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            bool processedAnyData = false;

            try
            {
                // Check if Cloud Sync is enabled in the database configuration
                var config = await _storageService.GetDeviceConfigAsync();
                if (config == null)
                {
                    await Task.Delay(2000, stoppingToken);
                    continue;
                }

                bool syncEnabled = config.IsSyncEnabled;
                string currentApiKey = config.ApiKey;

                if (syncEnabled)
                {
                    if (isSyncCurrentlyPaused)
                    {
                        _logger.LogInformation("[Sync] Cloud sync has been resumed. Draining queues...");
                        isSyncCurrentlyPaused = false;
                    }

                    string currentBaseUrl = config?.CloudEndpoint ?? "http://localhost:3000";

                    if (!string.IsNullOrEmpty(currentApiKey))
                    {
                        var step = await SyncTelemetryOnceAsync(config!, currentBaseUrl);
                        processedAnyData = step.ProcessedAny;
                        if (step.BackOff)
                        {
                            // Back off slightly on failure
                            await Task.Delay(3000, stoppingToken);
                        }
                    }
                }
                else
                {
                    if (!isSyncCurrentlyPaused)
                    {
                        _logger.LogWarning("[Sync] Cloud sync has been paused by the user. Telemetry is buffering inside SQLite.");
                        isSyncCurrentlyPaused = true;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in Sync Service worker loop.");
            }

            // If no data was present in the queues, sleep for 2 seconds before polling SQLite again
            if (!processedAnyData)
            {
                await Task.Delay(2000, stoppingToken);
            }
        }
    }

    /// <summary>What one telemetry step did: whether a batch was sent, and whether to back off.</summary>
    public readonly record struct TelemetryStep(bool ProcessedAny, bool BackOff);

    /// <summary>How the stored history-paused state must change after a delivered (202) batch.</summary>
    public enum HistoryPauseChange { None, Set, Clear }

    /// <summary>
    /// Edge contract §5.4. A 202 with the paused notice sets the state; a later 202 without it
    /// clears it. Identical repeated notices change nothing, so the operator sees one stable
    /// status rather than a stream of updates. Only delivered batches reach here: a failed
    /// request never announces recovery.
    /// </summary>
    public static HistoryPauseChange DecideHistoryPause(string? storedQuota, DateTime? storedSinceUtc, HistoryPauseNotice? notice)
    {
        bool storedPaused = !string.IsNullOrEmpty(storedQuota);
        if (notice == null) return storedPaused ? HistoryPauseChange.Clear : HistoryPauseChange.None;
        if (storedPaused && storedQuota == notice.Quota && storedSinceUtc == notice.SinceUtc) return HistoryPauseChange.None;
        return HistoryPauseChange.Set;
    }

    /// <summary>
    /// Sends one pending telemetry batch and settles it: per-batch semantics are unchanged.
    /// A 202 deletes the batch even when the cloud paused history (accepted = 0 is expected
    /// then; a retry would be dropped the same way, so there is no paused backlog to keep).
    /// 400 drops it, 401 keeps it and marks the key revoked, anything else keeps it for retry.
    /// </summary>
    public async Task<TelemetryStep> SyncTelemetryOnceAsync(Pulse.Edge.Storage.Models.DeviceConfig config, string baseUrl)
    {
        var telemetryBatch = await _storageService.GetPendingTelemetryBatchAsync(batchSize: 100);
        if (!telemetryBatch.Any()) return new TelemetryStep(false, false);

        var outcome = await _cloudClient.SendTelemetryBatchWithOutcomeAsync(baseUrl, config.Id, config.ApiKey, telemetryBatch);
        var ids = telemetryBatch.Select(x => x.Id).ToList();

        switch (outcome.Result)
        {
            case TelemetrySyncResult.Success:
                // Delete from local queue database
                await _storageService.CompleteTelemetryBatchAsync(ids);
                _logger.LogInformation("[Sync] Successfully synced and cleared {Count} telemetry records.", telemetryBatch.Count);
                await ApplyHistoryPauseAsync(config, outcome.HistoryPause);
                return new TelemetryStep(true, false);

            case TelemetrySyncResult.ClientError:
                // Remove from outbox because they are malformed and won't succeed on retry
                await _storageService.CompleteTelemetryBatchAsync(ids);
                _logger.LogError("[Sync] Telemetry upload failed with ClientError (400). Cleared {Count} records to avoid blocking the queue.", telemetryBatch.Count);
                return new TelemetryStep(true, false);

            case TelemetrySyncResult.Unauthorized:
                // Unlock records and increment retry counts (keep them queued)
                await _storageService.FailTelemetryBatchAsync(ids);
                _logger.LogError("[Sync] Telemetry upload failed with Unauthorized (401). API Key has been revoked. Stopping sync loop.");

                // Clear API key and mark status as Revoked
                config.ApiKey = "";
                config.SiteId = "";
                config.SiteName = "";
                config.CloudStatus = "Revoked";
                await _storageService.SaveDeviceConfigAsync(config);
                return new TelemetryStep(true, false);

            default: // TransientError
                // Unlock records and increment retry counts
                await _storageService.FailTelemetryBatchAsync(ids);
                _logger.LogWarning("[Sync] Telemetry upload failed with TransientError. Re-queued items for retry.");
                return new TelemetryStep(true, true);
        }
    }

    private async Task ApplyHistoryPauseAsync(Pulse.Edge.Storage.Models.DeviceConfig config, HistoryPauseNotice? notice)
    {
        switch (DecideHistoryPause(config.HistoryPausedQuota, config.HistoryPausedSince, notice))
        {
            case HistoryPauseChange.Set:
                await _storageService.SetHistoryPauseAsync(notice!.Quota, notice.SinceUtc);
                config.HistoryPausedQuota = notice.Quota;
                config.HistoryPausedSince = notice.SinceUtc;
                _logger.LogWarning(
                    "[Sync] PULSE Cloud paused history recording (quota: {Quota}, since: {Since}). Telemetry is still delivered, so live readings and alarms keep working, but history sent during the pause is not stored and will not be backfilled.",
                    notice.Quota, notice.SinceUtc?.ToString("o", System.Globalization.CultureInfo.InvariantCulture) ?? "unknown");
                break;
            case HistoryPauseChange.Clear:
                await _storageService.SetHistoryPauseAsync(string.Empty, null);
                config.HistoryPausedQuota = string.Empty;
                config.HistoryPausedSince = null;
                _logger.LogInformation("[Sync] PULSE Cloud is recording history again.");
                break;
        }
    }
}
