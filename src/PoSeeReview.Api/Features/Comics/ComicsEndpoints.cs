using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using PoSeeReview.Api.Middleware;
using PoSeeReview.Api.Storage;
using PoSeeReview.Api.Telemetry;
using PoSeeReview.Shared.Dtos;
using PoSeeReview.Shared.Contracts;
using PoSeeReview.Shared.Ids;
using PoSeeReview.Shared.Enums;

namespace PoSeeReview.Api.Features.Comics;

/// <summary>
/// Comic generation and retrieval slice. Maps <c>/api/comics</c> (NET_RULES 3.3).
/// </summary>
internal static class ComicsEndpoints
{
    public static IEndpointRouteBuilder MapComicsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/comics").WithTags("Comics").RequireValidPlaceId();

        // The one endpoint that spends money, so it carries the 3/min limiter.
        group.MapPost("/{placeId}/stream", GenerateComicStream).RequireRateLimiting("comics-post");

        // Literal segments outrank route parameters, so "/budget" and "/cached" are matched here
        // rather than being swallowed by "/{placeId}" below. Both are free reads and stay off
        // the limiter.
        group.MapGet("/budget", GetBudget);
        group.MapGet("/cached", GetCachedPlaceIds);
        group.MapGet("/{placeId}/image", DownloadComicImage).AddEndpointFilter(RefuseWithheld);
        group.MapGet("/{placeId}", GetCachedComic).AddEndpointFilter(RefuseWithheld);

        // The link-preview cards. Deliberately NOT under /api: UserAgentValidationMiddleware only
        // lets social crawlers through on non-/api paths, so an og:image under /api would be
        // fetched by exactly the clients that get a 400 there. Anonymous for the same reason —
        // a 401 on a preview fetch unfurls as the same blank card as no tags at all.
        // Output-cached because each one is an anonymous blob download plus a Skia render.
        // The shared policy keeps an entry for 60 seconds, which is also the longest a card for a
        // comic that has since been withheld can still be served from this cache.
        app.MapGet("/share/{placeId}/card.png", GetShareCard)
            .WithTags("Comics")
            .AllowAnonymous()
            .RequireValidPlaceId()
            .CacheOutput(SharedReadCachePolicy.Name);

        // Short links live in this slice because they only ever address a comic — see
        // ShareLinksEndpoints for why they are not under /api.
        app.MapShareLinkEndpoints();

        // The duel page is gone; links shared before it was retired land on the board instead of a 404.
        app.MapGet("/duel/{**rest}", () => Results.Redirect("/leaderboard"))
            .ExcludeFromDescription()
            .AllowAnonymous();

        return app;
    }

    /// <summary>
    /// Renders the 1200x630 image a shared link unfurls as.
    /// <para>
    /// <c>og:image</c> used to point at the comic's blob URL directly, and that URL carries a SAS
    /// signature that lapses in about a week. Every share older than the signature unfurled as a
    /// blank card, on a page whose Hall of Fame row is designed to last forever.
    /// </para>
    /// </summary>
    private static async Task<IResult> GetShareCard(
        string placeId,
        IComicGenerationService comicGenerationService,
        ShareCardService shareCardService,
        IContentModerationGate moderationGate,
        HttpContext http)
    {
        var comic = await ServableComicAsync(placeId, comicGenerationService, moderationGate, http.RequestAborted);

        return comic is null
            ? Results.NotFound()
            : CardResult(await shareCardService.RenderAsync(comic, http.RequestAborted), http);
    }

    /// <summary>
    /// The live comic for a card, or null when there is none or moderation is withholding it.
    /// A plain 404 follows, not a 451: this is a crawler-facing image, a withheld comic should
    /// simply produce no card, and a legal-reasons status inside an img tag is noise nobody reads.
    /// </summary>
    private static async Task<Comic?> ServableComicAsync(
        string placeId,
        IComicGenerationService comicGenerationService,
        IContentModerationGate moderationGate,
        CancellationToken cancellationToken)
    {
        var id = PlaceId.From(placeId);

        return (await moderationGate.EvaluateAsync(id, cancellationToken)).IsServable
            ? await comicGenerationService.GetCachedComicAsync(id, cancellationToken)
            : null;
    }

    private static IResult CardResult(byte[]? png, HttpContext http)
    {
        if (png is null)
        {
            return Results.NotFound();
        }

        // Long enough that a crawler re-fetching a popular link does not re-render it, short
        // enough that a takedown stops being served within the hour. The card is composed
        // from a comic, so it must not outlive one by much.
        http.Response.Headers.CacheControl = "public, max-age=3600";

        return Results.File(png, "image/png");
    }

    /// <summary>
    /// Withholds a comic read with a 451 when moderation says so. A filter rather than a line in
    /// each handler, so a new read under <c>/{placeId}</c> cannot forget it.
    /// </summary>
    private static async ValueTask<object?> RefuseWithheld(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var verdict = await http.RequestServices.GetRequiredService<IContentModerationGate>()
            .EvaluateAsync(PlaceId.From(http.Request.RouteValues["placeId"] as string), http.RequestAborted);

        return verdict.IsServable ? await next(context) : ModerationRefusal(verdict, http);
    }

    /// <summary>
    /// The 451 a caller gets when moderation has withheld a place.
    /// <para>
    /// 451 rather than 404: the comic is not missing, it is being withheld, and a moderator
    /// reading logs needs those two cases to be distinguishable. The detail deliberately does
    /// not name a report or a rule - that is operator information.
    /// </para>
    /// </summary>
    private static IResult ModerationRefusal(ModerationVerdict verdict, HttpContext http) => Results.Problem(
        type: "https://tools.ietf.org/html/rfc7725#section-3",
        title: "Unavailable For Legal Reasons",
        statusCode: StatusCodes.Status451UnavailableForLegalReasons,
        detail: WithheldDetail(verdict.IsSuppressed),
        instance: http.Request.Path);

    private static string WithheldDetail(bool isSuppressed) => isSuppressed
        ? "This restaurant has been removed from PoSeeReview after a takedown or a review."
        : "This comic is unavailable while it is being reviewed.";

    /// <summary>
    /// Maps a generation failure onto the status the client already knows how to render. The
    /// stream has committed a 200 by the time generation fails, so the status travels in the
    /// payload instead of a header.
    /// </summary>
    private static (int Status, string Title, string Detail, string TypeUri, string ErrorType) DescribeFailure(
        Exception ex,
        string placeId) => ex switch
    {
        KeyNotFoundException => (
            StatusCodes.Status404NotFound, "Not Found",
            $"Restaurant not found: {placeId}", "https://tools.ietf.org/html/rfc7231#section-6.5.4", "restaurant_not_found"),

        InsufficientReviewsException e => (
            StatusCodes.Status400BadRequest, "Bad Request",
            e.Message, "https://tools.ietf.org/html/rfc7231#section-6.5.1", "insufficient_reviews"),

        ContentBlockedException e => (
            StatusCodes.Status422UnprocessableEntity, "Unprocessable Entity",
            e.Message, "https://tools.ietf.org/html/rfc4918#section-11.2", "content_blocked"),

        ImageDeclinedException e => (
            StatusCodes.Status422UnprocessableEntity, "Unprocessable Entity",
            e.Message, "https://tools.ietf.org/html/rfc4918#section-11.2", "image_declined"),

        ContentWithheldException e => (
            StatusCodes.Status451UnavailableForLegalReasons, "Unavailable For Legal Reasons",
            WithheldDetail(e.IsSuppressed), "https://tools.ietf.org/html/rfc7725#section-3", "withheld"),

        InsufficientStrangenessException => (
            StatusCodes.Status422UnprocessableEntity, "Unprocessable Entity",
            "This restaurant's reviews are too ordinary to make a good comic. Try a place with weirder reviews!",
            "https://tools.ietf.org/html/rfc4918#section-11.2", "insufficient_strangeness"),

        _ => (
            StatusCodes.Status500InternalServerError, "Internal Server Error",
            "Comic generation failed. Please try again later.",
            "https://tools.ietf.org/html/rfc7231#section-6.6.1", "unknown")
    };

    private static void TrackFailure(string errorType, string placeId) =>
        PoSeeReviewTelemetry.ComicGenerationErrors.Add(1,
        [
            new KeyValuePair<string, object?>("error_type", errorType),
            new KeyValuePair<string, object?>("place_id", placeId)
        ]);

    private static void LogFailure(ILogger logger, Exception ex, string errorType, string placeId)
    {
        switch (errorType)
        {
            case "insufficient_strangeness":
                logger.LogInformation(ex, "Strangeness below threshold for placeId: {PlaceId}", placeId);
                break;
            case "unknown":
                logger.LogError(ex, "Failed to generate comic for placeId: {PlaceId}", placeId);
                break;
            default:
                logger.LogWarning(ex, "Comic generation rejected ({ErrorType}) for placeId: {PlaceId}", errorType, placeId);
                break;
        }
    }

    private static void RecordSuccessMetrics(Comic comic, bool forceRegenerate, string placeId, long startTimestamp, Activity? activity)
    {
        var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        var tags = new[]
        {
            new KeyValuePair<string, object?>("cache_hit", comic.CacheState == ComicCacheState.Cached),
            new KeyValuePair<string, object?>("force_regenerate", forceRegenerate)
        };

        PoSeeReviewTelemetry.ComicsGenerated.Add(1, tags);
        PoSeeReviewTelemetry.ComicGenerationDuration.Record(elapsedMs, tags);

        if (forceRegenerate)
        {
            PoSeeReviewTelemetry.ForceRegenerateRequests.Add(1,
            [
                new KeyValuePair<string, object?>("place_id", placeId)
            ]);
        }

        activity?.SetTag("comic_id", comic.Id.Value);
        activity?.SetTag("cache_hit", comic.CacheState == ComicCacheState.Cached);
        activity?.SetTag("duration_ms", elapsedMs);
    }

    /// <summary>
    /// Failures that are known to happen <em>before</em> the image model is called, so the
    /// reserved budget unit was never actually spent and must come back to the user. A generic
    /// 500 is deliberately absent: it can be thrown after the paid call, and refunding there
    /// would let a failing downstream burn quota for free.
    /// <para>
    /// <c>image_declined</c> is absent for exactly that reason: the image model answered, and a
    /// refusal is a billed response. The user is not charged twice — they are charged once, for a
    /// call that happened.
    /// </para>
    /// </summary>
    private static bool IsRefundableFailure(string errorType) =>
        errorType is "restaurant_not_found" or "insufficient_reviews" or "insufficient_strangeness"
            or "content_blocked";

    /// <summary>
    /// The 429 a caller gets when a daily ceiling — not the per-minute limiter — stopped them.
    /// Personal exhaustion and app-wide exhaustion read differently on purpose: only one of
    /// them is about something the user did.
    /// </summary>
    private static (string Title, string Detail) DescribeBudgetRefusal(BudgetReservation reservation) =>
        reservation.Decision == BudgetDecision.ServiceExhausted
            ? ("Service at Capacity",
               "PoSeeReview has hit its daily limit for drawing new comics. Existing comics still open normally, and the limit resets at midnight UTC.")
            : ("Daily Limit Reached",
               $"You have used all {reservation.Budget.DailyLimit} of today's comic generations. Your limit resets at midnight UTC — cached comics still open for free until then.");

    /// <summary>
    /// What the caller has left to spend today. A free read, so the client can grey out the
    /// generate button before a tap rather than after a 429.
    /// </summary>
    private static async Task<IResult> GetBudget(GenerationBudgetService budgetService, HttpContext http) =>
        Results.Ok(await budgetService.GetBudgetAsync(http.RequestAborted));

    /// <summary>
    /// Maximum place ids one lookup will accept. Each one is a point read, and discovery never
    /// shows more than a screenful.
    /// </summary>
    private const int MaxCachedLookupIds = 30;

    /// <summary>
    /// Which of these places already have a live comic.
    /// <para>
    /// Free, and the reason it exists is that a cache hit and a cache miss are wildly different
    /// products — one is instant and costs nothing, the other spends a paid image call and about
    /// ten seconds — and discovery had no way to tell a user which was which. The map view
    /// colours its pins from this.
    /// </para>
    /// <para>
    /// Point reads rather than a query: the ids come from the caller, and a filter built from
    /// caller input over a single partition is a scan waiting to be abused.
    /// </para>
    /// <para>
    /// It also carries the weirdness forecast for the places that are not drawn: the score their
    /// reviews last got, from the analysis cache. Nothing is computed for the request — search
    /// results carry no reviews, and fetching them per pin would cost more than the comic — so
    /// each sighting of a forecast place is also counted as demand for the nightly pre-draw.
    /// </para>
    /// </summary>
    private static async Task<IResult> GetCachedPlaceIds(
        string? placeIds,
        IComicGenerationService comicGenerationService,
        IContentModerationGate moderationGate,
        IAnalysisRepository analyses,
        Microsoft.Extensions.Options.IOptions<ComicOptions> comicOptions,
        HttpContext http)
    {
        var ids = (placeIds ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(PlaceId.IsValid)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxCachedLookupIds)
            .ToList();

        var options = comicOptions.Value;
        var now = DateTimeOffset.UtcNow;

        // Concurrent: each id is two or three independent point reads, and a screenful of them in
        // series was most of this endpoint's latency.
        var probes = await Task.WhenAll(ids.Select(async id =>
        {
            var placeId = PlaceId.From(id);

            // A withheld comic must not be advertised as instantly available: the pin would
            // promise a free result and the tap would land on a 451.
            if (!(await moderationGate.EvaluateAsync(placeId, http.RequestAborted)).IsServable)
            {
                return (Id: id, Score: (int?)null, Forecast: (int?)null);
            }

            var comic = await comicGenerationService.GetCachedComicAsync(placeId, http.RequestAborted);
            if (comic is not null && comic.ExpiresAt > now)
            {
                return (id, comic.StrangenessScore, null);
            }

            if (await analyses.GetAsync(placeId, http.RequestAborted) is { } analysis
                && analysis.PromptVersion == options.PromptVersion)
            {
                await analyses.RecordDemandAsync(placeId, http.RequestAborted);
                return (id, null, analysis.Analysis.StrangenessScore);
            }

            return (id, null, null);
        }));

        return Results.Ok(new CachedComicsResponse
        {
            CachedPlaceIds = [.. probes.Where(p => p.Score is not null).Select(p => p.Id)],
            Scores = probes.Where(p => p.Score is not null).ToDictionary(p => p.Id, p => p.Score!.Value, StringComparer.Ordinal),
            Forecasts = probes.Where(p => p.Forecast is not null).ToDictionary(p => p.Id, p => p.Forecast!.Value, StringComparer.Ordinal),
            ForecastFloor = options.MinimumStrangenessScore
        });
    }

    /// <summary>
    /// Comic generation, streamed as server-sent events so the client can narrate the stages the pipeline is genuinely in rather than animating a timer.
    /// One JSON envelope per <c>data:</c> line keeps the trimmed WASM parser trivial.
    /// </summary>
    private static async Task GenerateComicStream(
        string placeId,
        IComicGenerationService comicGenerationService,
        GenerationBudgetService budgetService,
        IContentModerationGate moderationGate,
        ILogger<ComicGenerationService> logger,
        HttpContext http,
        bool forceRegenerate = false)
    {
        http.Response.Headers.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache,no-store";
        // ARR/nginx will otherwise buffer the whole response and deliver every phase at once,
        // which still yields a correct comic but silently reduces this to the old fake spinner.
        http.Response.Headers["X-Accel-Buffering"] = "no";

        using var activity = PoSeeReviewTelemetry.ActivitySource.StartActivity("GenerateComicStream");
        activity?.SetTag("place_id", placeId);
        activity?.SetTag("force_regenerate", forceRegenerate);

        var startTime = Stopwatch.GetTimestamp();
        logger.LogInformation("Streaming comic generation for placeId: {PlaceId}, forceRegenerate: {ForceRegenerate}",
            placeId, forceRegenerate);

        // Moderation gate. The stream has already committed a 200 by this point, so the refusal
        // travels in the payload ErrorStatus rather than in a status line.
        var streamVerdict = await moderationGate.EvaluateAsync(PlaceId.From(placeId), http.RequestAborted);
        if (!streamVerdict.IsServable)
        {
            await WriteEventAsync(http, new ComicGenerationEventDto
            {
                Kind = ComicGenerationEventDto.ErrorKind,
                ErrorStatus = StatusCodes.Status451UnavailableForLegalReasons,
                ErrorTitle = "Unavailable For Legal Reasons",
                ErrorDetail = WithheldDetail(streamVerdict.IsSuppressed)
            });
            return;
        }

        // The daily spend ceiling. Checked before the first phase event
        // so a refused caller gets one error frame rather than a stepper that runs and then
        // fails — and so the stream cannot be used to spend past the cap.
        var reservation = await budgetService.TryReserveAsync(http.RequestAborted);
        if (!reservation.IsAllowed)
        {
            var (budgetTitle, budgetDetail) = DescribeBudgetRefusal(reservation);
            await WriteEventAsync(http, new ComicGenerationEventDto
            {
                Kind = ComicGenerationEventDto.ErrorKind,
                ErrorStatus = StatusCodes.Status429TooManyRequests,
                ErrorTitle = budgetTitle,
                ErrorDetail = budgetDetail
            });
            return;
        }

        // The pipeline reports phases from whatever thread it happens to be on. A channel makes
        // the response body single-writer (this method's drain loop) and preserves order —
        // writing to HttpResponse straight from IProgress.Report would race the completion write.
        var events = Channel.CreateUnbounded<ComicGenerationEventDto>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        var generation = ProduceAsync();

        try
        {
            await foreach (var evt in events.Reader.ReadAllAsync(http.RequestAborted))
            {
                await WriteEventAsync(http, evt);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // Client hung up mid-stream. The pipeline is not cancelled (see ProduceAsync); letting
            // this bubble would send UseExceptionHandler off to write a problem document to a
            // socket that is already gone.
            logger.LogInformation("Comic generation stream abandoned by the client for placeId: {PlaceId}", placeId);
        }

        await generation;

        async Task ProduceAsync()
        {
            try
            {
                // CancellationToken.None, not RequestAborted: the budget unit is reserved, so a
                // client that disconnects must not cancel the paid image call half-way — that
                // spends the money, refunds nothing and caches nothing. The pipeline runs to
                // completion and the next request finds the comic in the cache.
                var progress = new ChannelProgress(events.Writer);
                var comic = await comicGenerationService.GenerateComicAsync(
                    PlaceId.From(placeId), forceRegenerate, progress, CancellationToken.None);

                if (comic.CacheState == ComicCacheState.Cached)
                {
                    await budgetService.ReleaseAsync(CancellationToken.None);
                }

                RecordSuccessMetrics(comic, forceRegenerate, placeId, startTime, activity);
                logger.LogInformation("Comic generated successfully (stream) for placeId: {PlaceId}", placeId);

                events.Writer.TryWrite(new ComicGenerationEventDto
                {
                    Kind = ComicGenerationEventDto.CompleteKind,
                    Comic = comic.ToDto()
                });
            }
            catch (Exception ex)
            {
                var (status, title, detail, _, errorType) = DescribeFailure(ex, placeId);
                TrackFailure(errorType, placeId);
                LogFailure(logger, ex, errorType, placeId);

                if (IsRefundableFailure(errorType))
                {
                    await budgetService.ReleaseAsync(CancellationToken.None);
                }

                events.Writer.TryWrite(new ComicGenerationEventDto
                {
                    Kind = ComicGenerationEventDto.ErrorKind,
                    ErrorStatus = status,
                    ErrorType = errorType,
                    ErrorTitle = title,
                    ErrorDetail = detail
                });
            }
            finally
            {
                events.Writer.TryComplete();
            }
        }
    }

    /// <summary>
    /// camelCase to match the client's source-generated <c>AppJsonContext</c>, which is
    /// configured with <c>JsonKnownNamingPolicy.CamelCase</c>. A mismatch here deserializes
    /// silently into a default-valued envelope rather than throwing.
    /// </summary>
    private static readonly JsonSerializerOptions StreamJsonOptions = new(JsonSerializerDefaults.Web);

    private static async Task WriteEventAsync(HttpContext http, ComicGenerationEventDto evt)
    {
        var json = JsonSerializer.Serialize(evt, StreamJsonOptions);
        await http.Response.WriteAsync($"data: {json}\n\n", Encoding.UTF8);
        await http.Response.Body.FlushAsync();
    }

    /// <summary>
    /// Ordered, non-blocking progress sink. Deliberately not <see cref="Progress{T}"/>: with no
    /// SynchronizationContext (the ASP.NET Core case) that class posts each callback to the
    /// thread pool, so phases could be written out of order or overlap the completion event.
    /// </summary>
    private sealed class ChannelProgress(ChannelWriter<ComicGenerationEventDto> writer)
        : IProgress<ComicGenerationProgress>
    {
        public void Report(ComicGenerationProgress value) =>
            writer.TryWrite(new ComicGenerationEventDto
            {
                Kind = ComicGenerationEventDto.PhaseKind,
                Phase = value.Phase,
                Score = value.Score,
                Captions = value.Captions is null ? null : [.. value.Captions]
            });
    }

    /// <summary>
    /// Re-serves a comic image from this origin, so Share can attach it as a file and
    /// <c>/my-comics</c> can cache it offline.
    /// <para>
    /// The comic lives in Blob Storage on a different host that sends no CORS headers, so
    /// fetching it into a blob from the page fails outright.
    /// </para>
    /// <para>
    /// Deliberately not used for <em>displaying</em> the comic: that would put every view on the
    /// app's bandwidth instead of the CDN-adjacent blob URL, for no user-visible gain.
    /// </para>
    /// </summary>
    private static async Task<IResult> DownloadComicImage(
        string placeId,
        IComicGenerationService comicGenerationService,
        IBlobStorageService blobStorageService,
        HttpContext http)
    {
        var comic = await comicGenerationService.GetCachedComicAsync(PlaceId.From(placeId), http.RequestAborted);

        if (comic is null || string.IsNullOrWhiteSpace(comic.ImageUrl))
        {
            return Results.NotFound();
        }

        var stream = await blobStorageService.OpenComicImageStreamAsync(comic.ImageUrl, http.RequestAborted);
        if (stream is null)
        {
            return Results.NotFound();
        }

        // Sanitised so a restaurant name cannot inject header syntax or path separators into
        // Content-Disposition. Results.File quotes the value, but the name is third-party
        // text and this is the one place it reaches a header.
        // The blob's own extension says what it holds: .png for older comics, .webp since.
        var (extension, contentType) = Path.GetExtension(new Uri(comic.ImageUrl).AbsolutePath).ToLowerInvariant() switch
        {
            ".webp" => ("webp", "image/webp"),
            ".jpg" => ("jpg", "image/jpeg"),
            _ => ("png", "image/png")
        };
        var fileName = $"{BuildDownloadFileName(comic.RestaurantName)}.{extension}";

        return Results.File(stream, contentType, fileName, enableRangeProcessing: false);
    }

    /// <summary>
    /// Builds a safe, recognisable download filename from a restaurant name, keeping only
    /// letters, digits, spaces, hyphens and underscores.
    /// </summary>
    private static string BuildDownloadFileName(string restaurantName)
    {
        var cleaned = string.Join("_", restaurantName.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries))
            .Trim();

        if (string.IsNullOrWhiteSpace(cleaned))
        {
            cleaned = "restaurant";
        }

        return $"poseereview_{cleaned}";
    }

    /// <summary>
    /// Serves a comic from the local table cache. Free read — no Maps call, no AI generation.
    /// Returns 404 when expired, triggering a fresh generation on the client if desired. A miss
    /// is someone opening a place with no comic behind it — the strongest pre-draw demand there is.
    /// </summary>
    private static async Task<IResult> GetCachedComic(
        string placeId,
        IComicGenerationService comicGenerationService,
        IAnalysisRepository analyses,
        HttpContext http)
    {
        if (await comicGenerationService.GetCachedComicAsync(PlaceId.From(placeId), http.RequestAborted) is { } comic)
        {
            return Results.Ok(comic.ToDto(isCached: true));
        }

        await analyses.RecordDemandAsync(PlaceId.From(placeId), http.RequestAborted);

        return Results.Problem(
            type: "https://tools.ietf.org/html/rfc7231#section-6.5.4",
            title: "Not Found",
            statusCode: StatusCodes.Status404NotFound,
            detail: "No cached comic found",
            instance: http.Request.Path);
    }
}
