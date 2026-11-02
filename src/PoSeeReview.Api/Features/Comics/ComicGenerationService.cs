using PoSeeReview.Api.Features.Comics.Painters;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PoSeeReview.Api.Storage;
using PoSeeReview.Shared.Contracts;
using PoSeeReview.Shared.Ids;
using PoSeeReview.Shared.Enums;

namespace PoSeeReview.Api.Features.Comics;

/// <summary>
/// Orchestrates comic generation workflow: review fetching, strangeness analysis,
/// narrative creation, DALL-E image generation, and blob storage upload.
/// Prioritizes 1-star reviews as source material (most interesting stories).
/// Implements 7-day caching with ExpiresAt validation.
/// </summary>
public partial class ComicGenerationService : IComicGenerationService
{
    private readonly IRestaurantService _restaurantService;
    private readonly IChatCompletionService _chatService;
    private readonly IImageGenerationService _imageGenerationService;
    private readonly IComicTextOverlayService _comicTextOverlayService;
    private readonly IBlobStorageService _blobStorageService;
    private readonly IComicRepository _comicRepository;
    private readonly ILeaderboardService _leaderboardService;
    private readonly IContentSafetyScreener _contentSafetyScreener;
    private readonly IContentModerationGate _moderationGate;
    private readonly ILogger<ComicGenerationService> _logger;
    private readonly TelemetryClient _telemetryClient;
    private readonly TimeProvider _timeProvider;
    private readonly ComicGenerationLock _generationLock;
    private readonly IAnalysisRepository? _analysisRepository;
    private readonly ComicImageQaQueue? _imageQa;

    private readonly ComicOptions _options;

    public ComicGenerationService(
        IRestaurantService restaurantService,
        IChatCompletionService chatService,
        IImageGenerationService imageGenerationService,
        IComicTextOverlayService comicTextOverlayService,
        IBlobStorageService blobStorageService,
        IComicRepository comicRepository,
        ILeaderboardService leaderboardService,
        IContentSafetyScreener contentSafetyScreener,
        IContentModerationGate moderationGate,
        ILogger<ComicGenerationService> logger,
        TelemetryClient telemetryClient,
        ComicGenerationLock generationLock,
        IOptions<ComicOptions> options,
        IAnalysisRepository? analysisRepository = null,
        ComicImageQaQueue? imageQa = null,
        TimeProvider? timeProvider = null)
    {
        _analysisRepository = analysisRepository;
        _imageQa = imageQa;
        _restaurantService = restaurantService ?? throw new ArgumentNullException(nameof(restaurantService));
        _chatService = chatService ?? throw new ArgumentNullException(nameof(chatService));
        _imageGenerationService = imageGenerationService ?? throw new ArgumentNullException(nameof(imageGenerationService));
        _comicTextOverlayService = comicTextOverlayService ?? throw new ArgumentNullException(nameof(comicTextOverlayService));
        _blobStorageService = blobStorageService ?? throw new ArgumentNullException(nameof(blobStorageService));
        _comicRepository = comicRepository ?? throw new ArgumentNullException(nameof(comicRepository));
        _leaderboardService = leaderboardService ?? throw new ArgumentNullException(nameof(leaderboardService));
        _contentSafetyScreener = contentSafetyScreener ?? throw new ArgumentNullException(nameof(contentSafetyScreener));
        _moderationGate = moderationGate ?? throw new ArgumentNullException(nameof(moderationGate));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _telemetryClient = telemetryClient ?? throw new ArgumentNullException(nameof(telemetryClient));
        _generationLock = generationLock ?? throw new ArgumentNullException(nameof(generationLock));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// A cached comic is usable when it has not expired <em>and</em> was drawn under the prompt
    /// version currently in force.
    /// <para>
    /// The second half is what makes changing a prompt cheap to evaluate. Without it a tune kept
    /// serving the previous prompt's output until somebody paid for a forced regeneration, so the
    /// only way to see the new behaviour was to spend on every restaurant being compared — and
    /// the easy mistake was to compare new output against an old comic and blame the prompt.
    /// A version mismatch falls through to generation on demand, which needs no migration and no
    /// bulk job, and a row written before versioning existed reads back as 0 and so misses once.
    /// </para>
    /// </summary>
    private bool IsCacheUsable(Comic? comic) =>
        LiveComicLookup.IsLive(comic, _timeProvider.GetUtcNow(), _options.PromptVersion);

    /// <summary>
    /// Generates or retrieves cached comic for a restaurant.
    /// </summary>
    /// <param name="placeId">Google Maps place ID</param>
    /// <param name="forceRegenerate">If true, regenerates even if valid cache exists</param>
    /// <param name="progress">Optional sink for the stage currently running; best-effort, never awaited</param>
    /// <param name="cancellationToken">Cancels the generation pipeline</param>
    /// <returns>Generated or cached Comic entity</returns>
    /// <exception cref="KeyNotFoundException">If restaurant not found</exception>
    /// <exception cref="InsufficientReviewsException">If restaurant has fewer than required reviews</exception>
    public async Task<Comic> GenerateComicAsync(
        PlaceId placeId,
        bool forceRegenerate = false,
        IProgress<ComicGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (placeId.IsEmpty)
            throw new ArgumentException("PlaceId is required", nameof(placeId));

        _logger.GeneratingComic(placeId.Value, forceRegenerate);

        var overallStopwatch = Stopwatch.StartNew();
        var requestedAt = _timeProvider.GetUtcNow();

        if (!forceRegenerate
            && await _comicRepository.GetByPlaceIdAsync(placeId) is { } cachedComic
            && IsCacheUsable(cachedComic))
        {
            _logger.ReturningCachedComic(placeId.Value);
            return await ServeCachedAsync(cachedComic, "Comics.CacheHit", progress, overallStopwatch);
        }

        _telemetryClient.GetMetric("Comics.CacheMiss").TrackValue(1);

        // Single flight. The cache was read and missed a moment ago, and everything below spends
        // money. Two requests for one restaurant arriving inside that window — a double-tap on a
        // phone, two tabs — would both miss and both pay for the same comic, with the loser's
        // output discarded by the upsert. A per-place gate makes the second caller wait, re-read,
        // and find the first one's comic instead of commissioning its own.
        await using var generationGate = await _generationLock.AcquireAsync(placeId, cancellationToken);

        // Re-read under the gate: the wait may have been exactly as long as the other request's
        // generation, which is now in the cache. A forced regenerate reuses it too, but only if
        // it was drawn after this request arrived — that is the double-tap on Regenerate, and
        // anything older is the comic the caller asked to replace.
        var previous = await _comicRepository.GetByPlaceIdAsync(placeId);
        if (previous is not null && IsCacheUsable(previous) && (!forceRegenerate || previous.CreatedAt >= requestedAt))
        {
            _logger.LogInformation(
                "Comic for placeId {PlaceId} was generated while this request waited — serving it",
                placeId.Value);
            return await ServeCachedAsync(previous, "Comics.Generation.Deduplicated", progress, overallStopwatch);
        }

        // Fetch restaurant details with reviews
        progress?.Report(ComicGenerationPhase.FetchingReviews);
        Restaurant restaurant;
        try
        {
            restaurant = await _restaurantService.GetRestaurantByPlaceIdAsync(placeId, cancellationToken);
        }
        catch (KeyNotFoundException)
        {
            _logger.LogWarning("Restaurant not found: {PlaceId}", placeId);
            throw;
        }

        // Validate minimum review count
        var reviews = restaurant.Reviews ?? new List<Review>();
        if (reviews.Count < _options.MinimumReviewsRequired)
        {
            _logger.LogWarning("Insufficient reviews for placeId: {PlaceId}. Found {Count}, need {Minimum}",
                placeId, reviews.Count, _options.MinimumReviewsRequired);
            throw new InsufficientReviewsException(
                $"Restaurant must have at least {_options.MinimumReviewsRequired} reviews to generate a comic. Found {reviews.Count}.");
        }

        // Prioritize 1-star reviews (most interesting), then add higher ratings if needed
        var prioritizedReviews = PrioritizeReviewsByRating(reviews);

        _logger.LogInformation("Selected {OneStarCount} one-star reviews, {TwoStarCount} two-star reviews, {OtherCount} other reviews",
            prioritizedReviews.Count(r => r.Rating == 1),
            prioritizedReviews.Count(r => r.Rating == 2),
            prioritizedReviews.Count(r => r.Rating > 2));

        // Filtered only as far as Comics:ReviewFilter says (as written, by default). Ratings travel
        // with the text so a panel's cited review number can be turned back into a star rating.
        var filteredReviews = PrepareReviews(prioritizedReviews.Where(r => !string.IsNullOrWhiteSpace(r.Text)), _options.ReviewFilter);
        if (filteredReviews.Count < _options.MinimumReviewsRequired)
        {
            _logger.LogWarning("Insufficient appropriate reviews after filtering for placeId: {PlaceId}", placeId);
            throw new InsufficientReviewsException(
                $"Restaurant does not have enough appropriate reviews for comic generation.");
        }

        // Limit to top N reviews for analysis (cost control)
        var reviewsForAnalysis = filteredReviews.Take(_options.MaximumReviewsForAnalysis).ToList();

        // Analyze strangeness and generate narrative with panel count
        progress?.Report(ComicGenerationPhase.AnalyzingStrangeness);
        var analysisStopwatch = Stopwatch.StartNew();
        var (analysis, sources) = await AnalyzeAsync(placeId, restaurant.Name, reviewsForAnalysis, cancellationToken);
        analysisStopwatch.Stop();

        var strangenessScore = analysis.StrangenessScore;
        var panelCount = analysis.PanelCount;
        var narrative = analysis.Narrative;

        // The analyser is an external AI call: an empty or absent narrative is a plausible
        // response, not a programming error. Dereferencing it unguarded turned that into a
        // NullReferenceException surfaced to the user as "Object reference not set to an
        // instance of an object" on the comic page.
        if (string.IsNullOrWhiteSpace(narrative))
        {
            _logger.LogWarning("Strangeness analysis returned an empty narrative for placeId {PlaceId}", placeId);
            _telemetryClient.GetMetric("Comics.EmptyNarrative").TrackValue(1);
            throw new InsufficientReviewsException(
                "We couldn't turn these reviews into a story. Try another restaurant.");
        }

        _logger.LogInformation("Strangeness score: {Score}, Panel count: {PanelCount}, Narrative length: {Length}",
            strangenessScore, panelCount, narrative.Length);
        _telemetryClient.GetMetric("Comics.Generation.AnalysisDurationMs").TrackValue(analysisStopwatch.Elapsed.TotalMilliseconds);

        // Enforce the strangeness threshold BEFORE the expensive image-generation step (PRD: too
        // ordinary → 422, not a low-quality comic). Checking here also avoids burning an Imagen
        // call + blob write on a comic we would reject.
        if (strangenessScore < _options.MinimumStrangenessScore)
        {
            _logger.LogInformation("Strangeness {Score} below minimum {Minimum} for placeId {PlaceId} — rejecting as too ordinary",
                strangenessScore, _options.MinimumStrangenessScore, placeId);
            _telemetryClient.GetMetric("Comics.RejectedTooOrdinary").TrackValue(1);
            throw new InsufficientStrangenessException(strangenessScore, _options.MinimumStrangenessScore);
        }

        // Normalised here rather than at the overlay step so the wait for the artwork can show
        // them: they are final once the analysis returns, and the image call is the long pole.
        var captions = ChatPrompts.NormalizeCaptions(analysis.Captions, narrative, panelCount);
        var scenes = ChatPrompts.NormalizeScenes(analysis.Scenes, captions, panelCount);

        // Pre-publish content screen. Positioned here for two reasons: it is the first point at
        // which the model-authored prose exists, and it is still before the paid image call, so
        // a refusal costs nothing. The captions are screened with the narrative: they are the
        // words actually painted onto the image. So are the scenes, which publish as alt text.
        var screen = await _contentSafetyScreener.ScreenAsync(string.Join('\n', [narrative, .. captions, .. scenes]), cancellationToken);

        if (screen.IsBlocked)
        {
            _logger.LogWarning("Content screen blocked the narrative for placeId {PlaceId} ({Category})",
                placeId, screen.Category);
            _telemetryClient.GetMetric("Comics.ContentBlocked").TrackValue(1);
            // On the queue and on the owner's phone: a block is censorship, and is never silent.
            await _moderationGate.FlagForReviewAsync(placeId,
                ModerationCategories.Describe(ModerationCategories.Blocked, screen.Category ?? "unknown"), cancellationToken);
            throw new ContentBlockedException(screen.Category ?? "unknown");
        }

        if (screen.IsFlagged)
        {
            // Publishes, and lands in the moderation queue. Withholding on this signal alone
            // would refuse the app's best comics — allegation language is ordinary in the
            // one-star reviews the whole product mines.
            await _moderationGate.FlagForReviewAsync(placeId, screen.Category ?? "unknown", cancellationToken);
            _telemetryClient.GetMetric("Comics.ContentFlagged").TrackValue(1);
        }

        // Generate comic image (panel count capped at 2)
        progress?.Report(new ComicGenerationProgress(ComicGenerationPhase.GeneratingArtwork, strangenessScore, captions));
        var imageStopwatch = Stopwatch.StartNew();
        // Drawn as written unless Comics:BluntImagePrompts is on; then every swapped word is
        // reported to the owner. The stored scenes stay as written either way.
        IReadOnlyList<string> paintedScenes = scenes;
        if (_options.BluntImagePrompts)
        {
            (paintedScenes, var softened) = ComicImagePrompt.Blunt(scenes);
            if (softened.Count > 0)
            {
                _telemetryClient.GetMetric("Comics.Image.PromptTermsBlunted").TrackValue(softened.Count);
                await _moderationGate.FlagForReviewAsync(placeId,
                    ModerationCategories.Describe(ModerationCategories.PromptSoftened, string.Join(", ", softened)), cancellationToken);
            }
        }

        byte[] imageBytes;
        try
        {
            imageBytes = await _imageGenerationService.GenerateComicImageAsync(paintedScenes, cancellationToken);
        }
        catch (ImageDeclinedException ex)
        {
            // The image model refused to depict what the reviews describe. Nothing is published,
            // and the refusal lands in the moderation queue — which is the point: it is a signal
            // about the source material that nobody would otherwise ever see. This replaces a
            // fallback that answered a refusal by paying for a second image of an unrelated
            // cheerful restaurant and publishing it under these reviews' score.
            _telemetryClient.GetMetric("Comics.ImageDeclined").TrackValue(1);
            await _moderationGate.FlagForReviewAsync(placeId,
                ModerationCategories.Describe(ModerationCategories.ImageDeclined, ex.Reason.Length > 300 ? ex.Reason[..300] : ex.Reason), cancellationToken);
            _logger.LogWarning("Image model declined to draw placeId {PlaceId}: {Reason}", placeId.Value, ex.Reason);
            throw;
        }
        imageStopwatch.Stop();

        _logger.LogInformation("Generated {PanelCount}-panel comic image: {Size} bytes", panelCount, imageBytes.Length);
        _telemetryClient.GetMetric("Comics.Generation.ImageDurationMs").TrackValue(imageStopwatch.Elapsed.TotalMilliseconds);

        // Add readable text caption overlays to each panel (replaces garbled AI-rendered text).
        // The captions arrive with the analysis: one completion produces the score, the narrative
        // and the panel text together, so this step no longer waits on a model of its own. It was
        // the second paid round trip in a pipeline that only ever needed one.
        progress?.Report(ComicGenerationPhase.ComposingStrip);
        var overlayStopwatch = Stopwatch.StartNew();
        imageBytes = await _comicTextOverlayService.AddTextOverlayAsync(imageBytes, captions, panelCount, cancellationToken);
        overlayStopwatch.Stop();

        _logger.LogInformation("Added text overlay to comic: {Size} bytes", imageBytes.Length);
        _telemetryClient.GetMetric("Comics.Generation.TextOverlayDurationMs").TrackValue(overlayStopwatch.Elapsed.TotalMilliseconds);

        // Asked again, immediately before anything is stored. The verdict the endpoint read is
        // ten seconds old by now, and a takedown that landed during the draw would otherwise be
        // undone by the comic, board row and archive entry written below.
        var verdict = await _moderationGate.EvaluateAsync(placeId, cancellationToken);
        if (!verdict.IsServable)
        {
            _logger.LogWarning("PlaceId {PlaceId} was withheld during generation — nothing published", placeId.Value);
            throw new ContentWithheldException(verdict.IsSuppressed);
        }

        // Upload to blob storage
        progress?.Report(ComicGenerationPhase.Publishing);
        var comicId = ComicId.New();
        var blobUrl = await _blobStorageService.UploadComicImageAsync(comicId.Value, imageBytes);

        _logger.LogInformation("Uploaded comic to blob: {BlobUrl}", blobUrl);

        // Create comic entity with 7-day expiration
        var comic = new Comic
        {
            Id = comicId,
            PlaceId = placeId,
            RestaurantName = restaurant.Name,
            ImageUrl = blobUrl,
            Narrative = narrative,
            StrangenessScore = strangenessScore,
            CreatedAt = _timeProvider.GetUtcNow(),
            ExpiresAt = _timeProvider.GetUtcNow().AddDays(_options.CacheDurationDays),
            CacheState = ComicCacheState.Generated,
            Captions = [.. captions],
            Scenes = [.. scenes],
            PromptVersion = _options.PromptVersion,
            Breakdown = analysis.Ratings is { } r ? new ComicScoreBreakdown(r.Absurdity, r.Specificity, r.StoryPotential) : null,
            Sources = sources,
            ImageModel = _imageGenerationService.Model ?? string.Empty
        };

        // Save to cache
        await _comicRepository.UpsertAsync(comic);

        // Update leaderboard if score is high enough (service handles threshold check)
        try
        {
            var leaderboardEntry = new LeaderboardEntry
            {
                PlaceId = placeId,
                RestaurantName = restaurant.Name,
                Address = restaurant.Address,
                Region = restaurant.Region,
                StrangenessScore = strangenessScore,
                ComicBlobUrl = blobUrl,
                LastUpdated = DateTimeOffset.UtcNow,
                Latitude = restaurant.Latitude,
                Longitude = restaurant.Longitude
            };

            await _leaderboardService.UpsertEntryAsync(leaderboardEntry);
            _logger.LogInformation("Updated leaderboard for {PlaceId} with score {Score}", placeId, strangenessScore);
        }
        catch (Exception ex)
        {
            // Don't fail comic generation if leaderboard update fails
            _logger.LogWarning(ex, "Failed to update leaderboard for {PlaceId}", placeId);
        }

        // Every draw mints a new comic id, so the artwork it replaces is now unreferenced by the
        // comic row and the board. Best effort: DeleteBlobAsync logs instead of throwing.
        if (previous is not null && !string.IsNullOrWhiteSpace(previous.ImageUrl))
        {
            await _blobStorageService.DeleteBlobAsync(previous.ImageUrl);
        }

        // Sampled, off the request path: a vision model checks the artwork against the shot list.
        _imageQa?.TryEnqueue(new ComicImageQaRequest(placeId, imageBytes, scenes, captions, comic.ImageModel));

        _logger.ComicGenerationComplete(placeId.Value);

        _telemetryClient.TrackEvent("ComicGenerated", new Dictionary<string, string>
        {
            ["PlaceId"] = placeId.Value,
            ["RestaurantName"] = restaurant.Name,
            ["StrangenessScore"] = strangenessScore.ToString(),
            ["PanelCount"] = panelCount.ToString()
        });

        overallStopwatch.Stop();
        _telemetryClient.GetMetric("Comics.Generation.RequestDurationMs").TrackValue(overallStopwatch.Elapsed.TotalMilliseconds);

        return comic;
    }

    /// <summary>
    /// Explicit sexual terms. A review containing one is dropped: nothing about a restaurant
    /// visit needs it, and it is the category an image model refuses outright.
    /// </summary>
    [GeneratedRegex(
        @"\b(?:slut|whore|cock|penis|vagina|anus)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitContentRegex();

    /// <summary>
    /// Profanity, which is masked rather than dropped. Google returns at most five reviews and
    /// the angriest one-star review is usually the best material; dropping it over one word threw
    /// away the story to avoid the word. "damn", "hell" and "crap" are no longer on the list at
    /// all: they are ordinary review English, and "hell" is also a restaurant name.
    /// </summary>
    [GeneratedRegex(
        @"\b(?:fuck|shit|ass|bitch|bastard|piss|dick)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    internal static partial Regex ProfanityRegex();

    /// <summary>
    /// Drops reviews with explicit content and masks profanity in the rest. Both checks run on a
    /// leet-normalised copy so "sh1t" and "a$$" match like their plain spellings.
    /// </summary>
    private static List<string> FilterInappropriateReviews(List<string> reviews) =>
        [.. reviews.Select(review => review.Normalize(System.Text.NormalizationForm.FormC)).Where(IsPublishable).Select(MaskProfanity)];

    /// <summary>
    /// The reviews as the scorer sees them, filtered to <paramref name="filter"/>: as written,
    /// masked, or <see cref="FilterInappropriateReviews(List{string})"/>'s strict drop-and-mask.
    /// Stars travel beside the text.
    /// </summary>
    private static List<PreparedReview> PrepareReviews(IEnumerable<Review> reviews, ReviewFilter filter) =>
        [.. reviews
            .Select(review => (Text: review.Text.Normalize(System.Text.NormalizationForm.FormC), review.Rating))
            .Where(review => filter != ReviewFilter.Strict || IsPublishable(review.Text))
            .Select(review => new PreparedReview(filter == ReviewFilter.None ? review.Text : MaskProfanity(review.Text), review.Rating))];

    private static bool IsPublishable(string review) => !ExplicitContentRegex().IsMatch(NormalizeLeetSpeak(review));

    /// <summary>A review as the scorer sees it: masked text, plus the stars it was posted with.</summary>
    private sealed record PreparedReview(string Text, int Rating);

    /// <summary>
    /// The scorer's verdict on these reviews, from the analysis cache when the reviews and the
    /// prompt are unchanged — so a redraw keeps its score — and from the chat model otherwise.
    /// A verdict is cached even when it is too ordinary to draw: that is what lets discovery
    /// grey out a place before anyone pays to find out again.
    /// </summary>
    private async Task<(StrangenessAnalysis Analysis, ComicSource[] Sources)> AnalyzeAsync(
        PlaceId placeId, string restaurantName, List<PreparedReview> reviews, CancellationToken cancellationToken)
    {
        var hash = HashReviews(reviews);

        if (_analysisRepository is not null
            && await _analysisRepository.GetAsync(placeId, cancellationToken) is { } cached
            && cached.ReviewsHash == hash
            && cached.PromptVersion == _options.PromptVersion)
        {
            _telemetryClient.GetMetric("Comics.Analysis.CacheHit").TrackValue(1);
            return (cached.Analysis, cached.Sources);
        }

        _logger.LogInformation("Analyzing {Count} reviews for strangeness", reviews.Count);
        var analysis = await _chatService.AnalyzeStrangenessAsync([.. reviews.Select(r => r.Text)], cancellationToken);

        // The panel's citations are the only grounding signal there is; a panel citing nothing
        // is a scene the model may have invented, and its rate is what a prompt change should move.
        _telemetryClient.GetMetric("Comics.Analysis.UngroundedPanels").TrackValue(ChatPrompts.CountUngroundedPanels(analysis));

        ComicSource[] sources = [.. (analysis.PanelSources ?? [])
            .SelectMany((ids, panel) => ids
                .Where(id => id >= 1 && id <= reviews.Count)
                .Select(id => new ComicSource(panel + 1, id, reviews[id - 1].Rating)))];

        if (_analysisRepository is not null && !string.IsNullOrWhiteSpace(analysis.Narrative))
        {
            try
            {
                await _analysisRepository.SaveAsync(
                    new CachedAnalysis(placeId, restaurantName, hash, _options.PromptVersion, analysis, sources, Demand: 0),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                // A cache write is an optimisation; the comic does not wait on it.
                _logger.LogWarning(ex, "Failed to cache the analysis for {PlaceId}", placeId.Value);
            }
        }

        return (analysis, sources);
    }

    /// <summary>What the scorer was shown, in order. A change to any review re-asks the scorer.</summary>
    private static string HashReviews(List<PreparedReview> reviews) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join("\u001f", reviews.Select(r => $"{r.Rating}|{r.Text}")))))[..32];

    /// <summary>
    /// Matched on the normalised copy, masked in the original. The substitutions are one
    /// character for one, so a match index in one is the same index in the other.
    /// </summary>
    private static string MaskProfanity(string review)
    {
        var matches = ProfanityRegex().Matches(NormalizeLeetSpeak(review));
        if (matches.Count == 0)
            return review;

        var masked = new System.Text.StringBuilder(review);
        for (var i = matches.Count - 1; i >= 0; i--)
        {
            masked.Remove(matches[i].Index, matches[i].Length).Insert(matches[i].Index, "[bleep]");
        }

        return masked.ToString();
    }

    /// <summary>
    /// Normalizes common leet-speak substitutions, strictly one character for one (see
    /// <see cref="MaskProfanity"/>). There is no rule for "*": a starred word is already masked.
    /// </summary>
    internal static string NormalizeLeetSpeak(string text)
    {
        return text
            .Replace('0', 'o')
            .Replace('1', 'i')
            .Replace('3', 'e')
            .Replace('4', 'a')
            .Replace('5', 's')
            .Replace('7', 't')
            .Replace('@', 'a')
            .Replace('$', 's')
            .Replace('!', 'i');
    }

    /// <summary>
    /// Every negative review (1-3 stars) first, lowest rating then longest text; positive reviews
    /// only top the list up to <see cref="ComicOptions.MaximumReviewsForAnalysis"/>. Google
    /// returns at most five reviews per place, so this orders what there is.
    /// </summary>
    private List<Review> PrioritizeReviewsByRating(List<Review> reviews)
    {
        var byRatingThenLength = reviews.OrderBy(r => r.Rating).ThenByDescending(r => r.Text?.Length ?? 0).ToList();
        var negative = byRatingThenLength.Where(r => r.Rating <= 3).ToList();

        return [.. negative, .. byRatingThenLength
            .Where(r => r.Rating >= 4)
            .Take(Math.Max(0, _options.MaximumReviewsForAnalysis - negative.Count))];
    }

    /// <summary>
    /// Gets cached comic for a restaurant if it exists and hasn't expired
    /// </summary>
    public async Task<Comic?> GetCachedComicAsync(PlaceId placeId, CancellationToken cancellationToken = default)
    {
        if (placeId.IsEmpty)
            throw new ArgumentException("PlaceId is required", nameof(placeId));

        var cachedComic = await _comicRepository.GetByPlaceIdAsync(placeId);

        return cachedComic is not null && IsCacheUsable(cachedComic) ? await AsCachedAsync(cachedComic) : null;
    }

    /// <summary>Marks a usable cached comic as such, re-signing its image URL when the SAS is about to lapse.</summary>
    private async Task<Comic> AsCachedAsync(Comic comic)
    {
        if (SasUrl.IsExpiringSoon(comic.ImageUrl, treatUnsignedAzureUrlAsStale: true))
        {
            _logger.LogInformation("SAS token for cached comic {PlaceId} is expired/expiring — refreshing", comic.PlaceId);
            comic.ImageUrl = await _blobStorageService.RefreshSasUrlAsync(comic.ImageUrl);
            await _comicRepository.UpsertAsync(comic);
        }

        comic.CacheState = ComicCacheState.Cached;
        return comic;
    }

    private async Task<Comic> ServeCachedAsync(
        Comic comic, string metricName, IProgress<ComicGenerationProgress>? progress, Stopwatch stopwatch)
    {
        var served = await AsCachedAsync(comic);
        progress?.Report(ComicGenerationPhase.CacheHit);
        _telemetryClient.GetMetric(metricName).TrackValue(1);
        _telemetryClient.GetMetric("Comics.Generation.RequestDurationMs").TrackValue(stopwatch.Elapsed.TotalMilliseconds);
        return served;
    }
}
