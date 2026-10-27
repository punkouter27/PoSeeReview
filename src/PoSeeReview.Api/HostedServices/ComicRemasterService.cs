using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Options;
using PoSeeReview.Api.Features.Comics;
using PoSeeReview.Api.Storage;
using PoSeeReview.Shared.Contracts;
using PoSeeReview.Shared.Enums;

namespace PoSeeReview.Api.HostedServices;

/// <summary><c>Remaster</c> configuration: which comics earn the premium painter, and how many per run.</summary>
public sealed class RemasterOptions
{
    public const string SectionName = "Remaster";

    /// <summary>DI key of the premium <see cref="IImageGenerationService"/>, chosen by <c>Ai:RemasterProvider</c>.</summary>
    public const string ServiceKey = "remaster";

    public bool Enabled { get; set; }

    /// <summary>How deep into the live board a comic must be to earn a redraw.</summary>
    public int TopN { get; set; } = 10;

    /// <summary>Redraws per run. With <see cref="IntervalMinutes"/> this is the daily spend ceiling.</summary>
    public int MaxPerRun { get; set; } = 3;

    public int IntervalMinutes { get; set; } = 360;
}

/// <summary>
/// Draft cheap, remaster what gets popular.
/// <para>
/// Every comic is drawn first by the cheap painter (<c>Ai:ImageProvider</c>). Most are opened by
/// the person who asked and nobody else, so paying premium for each one paid premium for views
/// that never happen. Comics that reach the top of the live board are where the views are; this
/// redraws those with the premium painter from the <em>same</em> scenes and captions, so the
/// score, the story and the board position do not move — only the artwork improves.
/// </para>
/// <para>
/// The new image overwrites the same blob, so the board row and the Hall of Fame entry, which
/// point at that blob, show the remaster with no second write. One attempt per comic: a refusal
/// or a failure is recorded and never retried, because a failed image call may have been billed.
/// </para>
/// </summary>
public sealed class ComicRemasterService(
    IServiceScopeFactory scopeFactory,
    IOptions<RemasterOptions> options,
    IOptions<ComicOptions> comicOptions,
    ComicGenerationLock generationLock,
    TelemetryClient telemetry,
    TimeProvider timeProvider,
    ILogger<ComicRemasterService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromMinutes(5), timeProvider, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Remaster pass failed");
                }

                await Task.Delay(TimeSpan.FromMinutes(Math.Max(15, options.Value.IntervalMinutes)), timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    internal async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var services = scope.ServiceProvider;

        var painter = services.GetKeyedService<IImageGenerationService>(RemasterOptions.ServiceKey);
        if (painter is null)
        {
            return 0;
        }

        var leaderboard = services.GetRequiredService<ILeaderboardRepository>();
        var comics = services.GetRequiredService<IComicRepository>();
        var overlay = services.GetRequiredService<IComicTextOverlayService>();
        var blobs = services.GetRequiredService<IBlobStorageService>();
        var gate = services.GetRequiredService<IContentModerationGate>();

        var settings = options.Value;
        var remastered = 0;

        foreach (var entry in await leaderboard.GetTopEntriesGlobalAsync(Math.Max(1, settings.TopN)))
        {
            if (remastered >= settings.MaxPerRun)
            {
                break;
            }

            if (!IsCandidate(await comics.GetByPlaceIdAsync(entry.PlaceId), painter.Model)
                || !(await gate.EvaluateAsync(entry.PlaceId, cancellationToken)).IsServable)
            {
                continue;
            }

            // Under the generation gate so a Regenerate cannot land between the read and the write.
            await using var placeGate = await generationLock.AcquireAsync(entry.PlaceId, cancellationToken);
            if (await comics.GetByPlaceIdAsync(entry.PlaceId) is not { } comic || !IsCandidate(comic, painter.Model))
            {
                continue;
            }

            if (await RemasterAsync(comic, painter, overlay, blobs, gate, comics, cancellationToken))
            {
                remastered++;
            }
        }

        return remastered;
    }

    private bool IsCandidate(Comic? comic, string remasterModel) =>
        comic is not null
        && LiveComicLookup.IsLive(comic, timeProvider.GetUtcNow(), comicOptions.Value.PromptVersion)
        && comic.RemasterState == RemasterState.None
        // Empty means drawn before the model was recorded — mostly by the premium painter already.
        && !string.IsNullOrWhiteSpace(comic.ImageModel)
        && !string.Equals(comic.ImageModel, remasterModel, StringComparison.OrdinalIgnoreCase)
        && comic.Scenes.Length > 0;

    private async Task<bool> RemasterAsync(
        Comic comic,
        IImageGenerationService painter,
        IComicTextOverlayService overlay,
        IBlobStorageService blobs,
        IContentModerationGate gate,
        IComicRepository comics,
        CancellationToken cancellationToken)
    {
        byte[] image;
        try
        {
            image = await painter.GenerateComicImageAsync(comic.Scenes, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Recorded, not retried. A premium refusal leaves the draft — which the cheap painter
            // already drew from the same scenes — in place.
            comic.RemasterState = ex is ImageDeclinedException ? RemasterState.Declined : RemasterState.Failed;
            await comics.UpsertAsync(comic);
            telemetry.GetMetric("Comics.Remaster.Failed", "State").TrackValue(1, comic.RemasterState.ToString());
            logger.LogWarning(ex, "Remaster of {PlaceId} did not complete ({State})", comic.PlaceId.Value, comic.RemasterState);
            return false;
        }

        var bytes = await overlay.AddTextOverlayAsync(image, comic.Captions, comic.Scenes.Length, cancellationToken);

        // A takedown during the draw wins, exactly as it does for a fresh comic.
        if (!(await gate.EvaluateAsync(comic.PlaceId, cancellationToken)).IsServable)
        {
            return false;
        }

        var url = await blobs.UploadComicImageAsync(comic.Id.Value, bytes);
        if (!SameBlob(url, comic.ImageUrl))
        {
            // A different extension means the overlay fell back to the raw image: a new blob the
            // board and archive do not point at. Keep the draft rather than split the references.
            await blobs.DeleteBlobAsync(url);
            comic.RemasterState = RemasterState.Failed;
            await comics.UpsertAsync(comic);
            return false;
        }

        var draftModel = comic.ImageModel;
        comic.ImageUrl = url;
        comic.ImageModel = painter.Model;
        comic.RemasterState = RemasterState.Remastered;
        await comics.UpsertAsync(comic);

        telemetry.GetMetric("Comics.Remastered", "From", "To").TrackValue(1, draftModel, painter.Model);
        logger.LogInformation("Remastered {PlaceId} ({Restaurant}): {From} -> {To}",
            comic.PlaceId.Value, comic.RestaurantName, draftModel, painter.Model);
        return true;
    }

    private static bool SameBlob(string a, string b) =>
        Uri.TryCreate(a, UriKind.Absolute, out var left)
        && Uri.TryCreate(b, UriKind.Absolute, out var right)
        && string.Equals(left.AbsolutePath, right.AbsolutePath, StringComparison.OrdinalIgnoreCase);
}
