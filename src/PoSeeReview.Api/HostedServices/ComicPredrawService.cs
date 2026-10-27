using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Options;
using PoSeeReview.Api.Features.Comics;
using PoSeeReview.Shared.Contracts;

namespace PoSeeReview.Api.HostedServices;

/// <summary><c>Predraw</c> configuration: which places are drawn before anyone asks.</summary>
public sealed class PredrawOptions
{
    public const string SectionName = "Predraw";

    public bool Enabled { get; set; }

    /// <summary>Minimum cached score: only places that will make a good comic are worth drawing unasked.</summary>
    public int MinScore { get; set; } = 40;

    /// <summary>Minimum sightings (search pins, comic-page misses) since the place was analysed.</summary>
    public int MinDemand { get; set; } = 3;

    /// <summary>Comics per run, and with <see cref="IntervalHours"/> the daily spend ceiling.</summary>
    public int MaxPerRun { get; set; } = 5;

    public int IntervalHours { get; set; } = 24;
}

/// <summary>
/// Draws comics for places people keep looking for, before the next one taps.
/// <para>
/// A cache hit is instant and free; a miss is ten seconds and a paid image. The analysis cache
/// already knows which places score well and how often they come up in searches or get opened
/// with no comic behind them — typically a popular place whose comic has expired. Drawing those
/// overnight turns the next visitor's ten-second wait into an instant open, and moves the spend
/// off the busy hours, where the 3/min generation limit bites.
/// </para>
/// <para>
/// Real-time calls rather than a provider batch API: with drafts on the cheap painter a comic is
/// about half a cent, and the batch discount would save fractions of a cent per comic at the price
/// of a second pipeline that finishes up to a day later.
/// </para>
/// </summary>
public sealed class ComicPredrawService(
    IServiceScopeFactory scopeFactory,
    IOptions<PredrawOptions> options,
    IOptions<ComicOptions> comicOptions,
    TelemetryClient telemetry,
    TimeProvider timeProvider,
    ILogger<ComicPredrawService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromMinutes(10), timeProvider, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Pre-draw pass failed");
                }

                await Task.Delay(TimeSpan.FromHours(Math.Max(1, options.Value.IntervalHours)), timeProvider, stoppingToken);
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
        var analyses = services.GetRequiredService<IAnalysisRepository>();
        var comics = services.GetRequiredService<IComicRepository>();
        var gate = services.GetRequiredService<IContentModerationGate>();
        var generator = services.GetRequiredService<IComicGenerationService>();

        var settings = options.Value;
        var promptVersion = comicOptions.Value.PromptVersion;
        var minScore = Math.Max(settings.MinScore, comicOptions.Value.MinimumStrangenessScore);

        var candidates = await analyses.GetPredrawCandidatesAsync(
            promptVersion, minScore, settings.MinDemand, settings.MaxPerRun * 4, cancellationToken);

        var drawn = 0;
        foreach (var candidate in candidates)
        {
            if (drawn >= settings.MaxPerRun)
            {
                break;
            }

            if (LiveComicLookup.IsLive(await comics.GetByPlaceIdAsync(candidate.PlaceId), timeProvider.GetUtcNow(), promptVersion)
                || !(await gate.EvaluateAsync(candidate.PlaceId, cancellationToken)).IsServable)
            {
                continue;
            }

            // Before the attempt, not after: a refusal or a failure must not come back every night
            // and bill the same image again. The place has to be wanted again to be tried again.
            await analyses.ResetDemandAsync(candidate.PlaceId, cancellationToken);

            try
            {
                // The ordinary pipeline: same review fetch, analysis cache, screen, gate and board.
                await generator.GenerateComicAsync(candidate.PlaceId, cancellationToken: cancellationToken);
                drawn++;
                logger.LogInformation("Pre-drew {PlaceId} ({Restaurant}), score {Score}, demand {Demand}",
                    candidate.PlaceId.Value, candidate.RestaurantName, candidate.Analysis.StrangenessScore, candidate.Demand);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One attempt per run, like any generation. The next run re-reads the candidates.
                logger.LogWarning(ex, "Pre-draw of {PlaceId} failed", candidate.PlaceId.Value);
            }
        }

        telemetry.GetMetric("Comics.Predrawn").TrackValue(drawn);
        return drawn;
    }
}
