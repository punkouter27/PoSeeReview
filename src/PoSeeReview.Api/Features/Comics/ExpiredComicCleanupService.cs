using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PoSeeReview.Api.Storage;
using PoSeeReview.Shared.Contracts;
using PoSeeReview.Shared.Ids;
using PoSeeReview.Shared.Enums;

namespace PoSeeReview.Api.Features.Comics;

/// <summary>
/// Periodically purges expired comics and their associated blobs
/// to control storage costs and respect 24-hour cache policy.
/// </summary>
public class ExpiredComicCleanupService : BackgroundService
{
    private const int DefaultBatchSize = 25;
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ExpiredComicCleanupService> _logger;
    private readonly TelemetryClient _telemetryClient;
    private readonly TimeSpan _interval;
    private readonly int _batchSize;

    public ExpiredComicCleanupService(
        IServiceScopeFactory scopeFactory,
        ILogger<ExpiredComicCleanupService> logger,
        TelemetryClient telemetryClient,
        IConfiguration configuration)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _telemetryClient = telemetryClient ?? throw new ArgumentNullException(nameof(telemetryClient));

        var intervalMinutes = configuration.GetValue("Cleanup:ExpiredComicIntervalMinutes", (int)DefaultInterval.TotalMinutes);
        _interval = intervalMinutes <= 0
            ? DefaultInterval
            : TimeSpan.FromMinutes(Math.Min(intervalMinutes, 720));

        _batchSize = Math.Clamp(configuration.GetValue("Cleanup:ExpiredComicBatchSize", DefaultBatchSize), 5, 200);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial delay gives the application time to finish bootstrapping
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanupExpiredComicsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Ignore cancellation requests during shutdown
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected failure during expired comic cleanup");
                _telemetryClient.TrackException(ex, new Dictionary<string, string>
                {
                    ["Component"] = nameof(ExpiredComicCleanupService)
                });
            }

            try
            {
                await Task.Delay(_interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task CleanupExpiredComicsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var comicRepository = scope.ServiceProvider.GetRequiredService<IComicRepository>();
        var blobStorageService = scope.ServiceProvider.GetRequiredService<IBlobStorageService>();
        var leaderboardRepository = scope.ServiceProvider.GetRequiredService<ILeaderboardRepository>();

        var totalDeleted = 0;
        var iterationStopwatch = Stopwatch.StartNew();

        while (!cancellationToken.IsCancellationRequested)
        {
            var expiredComics = await comicRepository.GetExpiredComicsAsync(DateTimeOffset.UtcNow, _batchSize, cancellationToken);

            if (expiredComics.Count == 0)
            {
                break;
            }

            var deletedThisPass = 0;

            foreach (var comic in expiredComics)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    // Blob before row: the row is the only pointer to the blob, so deleting it
                    // first turns a failed blob delete into an orphan nothing ever retries.
                    if (!comic.Id.IsEmpty)
                    {
                        // A blob the board row points at is the one the weekly archive filed.
                        var leaderboardEntry = await leaderboardRepository.GetByPlaceIdAsync(comic.PlaceId);
                        var blobIsReferencedByLeaderboard =
                            leaderboardEntry != null &&
                            !string.IsNullOrWhiteSpace(leaderboardEntry.ComicBlobUrl) &&
                            leaderboardEntry.ComicBlobUrl.Contains(comic.Id.Value, StringComparison.OrdinalIgnoreCase);

                        if (blobIsReferencedByLeaderboard)
                        {
                            // The live board mirrors the live comic, so the row goes with it. The
                            // blob stays: the weekly archive still shows this artwork.
                            await leaderboardRepository.DeleteAsync(leaderboardEntry!.PlaceId, leaderboardEntry.Region);
                            _logger.LogInformation(
                                "Removed {PlaceId} from the live board; kept blob {ComicId} for the weekly archive",
                                comic.PlaceId.Value, comic.Id.Value);
                        }
                        else
                        {
                            await blobStorageService.DeleteComicImageAsync(comic.Id.Value);
                        }
                    }

                    await comicRepository.DeleteAsync(comic.PlaceId);
                    deletedThisPass++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete expired comic {ComicId} for {PlaceId}", comic.Id.Value, comic.PlaceId.Value);
                    _telemetryClient.TrackException(ex, new Dictionary<string, string>
                    {
                        ["ComicId"] = comic.Id.Value,
                        ["PlaceId"] = comic.PlaceId.Value
                    });
                }
            }

            totalDeleted += deletedThisPass;

            // A pass that deleted nothing would refetch the same failing batch forever.
            if (deletedThisPass == 0 || expiredComics.Count < _batchSize)
            {
                break;
            }
        }

        iterationStopwatch.Stop();

        if (totalDeleted > 0)
        {
            _logger.LogInformation("Expired comic cleanup removed {Count} records in {Duration}ms", totalDeleted, iterationStopwatch.Elapsed.TotalMilliseconds);
        }

        _telemetryClient.GetMetric("Comics.Expired.Purged").TrackValue(totalDeleted);
        _telemetryClient.GetMetric("Comics.Expired.CleanupDurationMs").TrackValue(iterationStopwatch.Elapsed.TotalMilliseconds);
    }
}
