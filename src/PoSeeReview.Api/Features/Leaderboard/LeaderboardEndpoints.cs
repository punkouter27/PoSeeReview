using System.Text.RegularExpressions;
using Azure;
using PoSeeReview.Api.Middleware;
using PoSeeReview.Api.Storage;
using PoSeeReview.Shared.Dtos;
using PoSeeReview.Shared.Contracts;
using PoSeeReview.Shared.Ids;
using PoSeeReview.Shared.Enums;

namespace PoSeeReview.Api.Features.Leaderboard;

/// <summary>
/// Global strangeness leaderboard slice. Maps <c>/api/leaderboard</c> (NET_RULES 3.3).
/// </summary>
internal static partial class LeaderboardEndpoints
{
    /// <summary>
    /// Accepted region shape: a country code optionally narrowed by subdivisions
    /// (<c>US</c>, <c>US-WA</c>, <c>US-WA-Seattle</c>). Source-generated and shared by both
    /// handlers rather than re-parsed per request.
    /// </summary>
    [GeneratedRegex(@"^[A-Z]{2}(-[A-Z0-9]+)*$", RegexOptions.IgnoreCase)]
    private static partial Regex RegionFormatRegex();

    private static Regex RegionFormat => RegionFormatRegex();

    public static IEndpointRouteBuilder MapLeaderboardEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/leaderboard").WithTags("Leaderboard");

        group.MapGet("", GetLeaderboard).CacheOutput(SharedReadCachePolicy.Name);

        // Literal segment, so it is matched ahead of anything parameterised in this group.
        // Cached because every entry costs a blob existence probe.
        group.MapGet("/weekly", GetWeeklyHallOfFame).CacheOutput(SharedReadCachePolicy.Name);

        return app;
    }

    /// <summary>Weeks of archive a single request may ask for.</summary>
    private const int MaxWeeks = 12;

    /// <summary>
    /// The permanent weekly archive. Unlike the live board, these rows outlive the 24-hour
    /// comic they came from — which is the entire point, and also why an entry can carry a
    /// blob URL that no longer resolves.
    /// </summary>
    private static async Task<IResult> GetWeeklyHallOfFame(
        HallOfFameRepository hallOfFame,
        IBlobStorageService blobStorageService,
        IContentModerationGate moderationGate,
        ILogger<HallOfFameRepository> logger,
        TimeProvider timeProvider,
        HttpContext http,
        int weeks = 4,
        int limit = 10,
        string? region = null,
        string? category = null)
    {
        if (!PlaceCategories.TryParse(category, out var kind))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid category",
                detail: "Category must be restaurant or apartment.");
        }
        // Same sentinel as the live board: absent means every region's archive, merged per week.
        var isGlobal = string.IsNullOrWhiteSpace(region);

        if (!isGlobal && !RegionFormat.IsMatch(region!))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid region",
                detail: $"Region '{region}' has invalid format. Must start with a 2-letter country code (e.g., US, GB, US-WA).");
        }

        if (weeks < 1 || weeks > MaxWeeks)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid weeks", detail: $"Weeks must be between 1 and {MaxWeeks}");
        }

        if (limit < 1 || limit > 50)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid limit", detail: "Limit must be between 1 and 50");
        }

        var regionCode = isGlobal ? new RegionCode(string.Empty) : RegionCode.From(region);
        var now = timeProvider.GetUtcNow();
        var response = new HallOfFameResponse { Region = isGlobal ? "ALL" : region!.ToUpperInvariant(), Category = kind.ToWire() };
        var withheld = new Dictionary<string, bool>(StringComparer.Ordinal);

        for (var offset = 0; offset < weeks; offset++)
        {
            var instant = now.AddDays(-7 * offset);
            var weekKey = HallOfFameEntity.WeekKeyFor(instant);

            var entities = isGlobal
                ? await hallOfFame.GetWeekGlobalAsync(weekKey, limit, kind, http.RequestAborted)
                : await hallOfFame.GetWeekAsync(regionCode, weekKey, limit, kind, http.RequestAborted);

            var week = new HallOfFameWeekDto
            {
                WeekKey = weekKey,
                WeekStart = HallOfFameEntity.WeekStartFor(weekKey)
            };

            var rank = 1;
            foreach (var entity in entities)
            {
                // The archive outlives the comic, so a withheld place would otherwise stay
                // listed here by name and score. Fails open, like every other gate read.
                if (!withheld.TryGetValue(entity.PlaceId, out var isWithheld))
                {
                    var verdict = await moderationGate.EvaluateAsync(PlaceId.From(entity.PlaceId), http.RequestAborted);
                    withheld[entity.PlaceId] = isWithheld = !verdict.IsServable;
                }

                if (isWithheld)
                {
                    continue;
                }

                var imageGone = await IsImageGoneAsync(blobStorageService, logger, entity.ComicBlobUrl);

                week.Entries.Add(new HallOfFameEntryDto
                {
                    Rank = rank++,
                    PlaceId = entity.PlaceId,
                    RestaurantName = entity.RestaurantName,
                    Address = entity.Address,
                    Region = entity.Region,
                    StrangenessScore = entity.StrangenessScore,
                    ComicBlobUrl = imageGone
                        ? entity.ComicBlobUrl
                        : await FreshReadUrlAsync(blobStorageService, logger, entity.ComicBlobUrl),
                    ArchivedAt = entity.ArchivedAt,
                    ImageExpired = imageGone
                });
            }

            // Empty weeks are skipped rather than rendered: a column of "nothing happened"
            // headings is worse than a shorter archive.
            if (week.Entries.Count > 0)
            {
                response.Weeks.Add(week);
            }
        }

        return Results.Ok(response);
    }

    /// <summary>
    /// The archive row keeps the SAS it was written with, and an archive outlives a SAS by
    /// design — so a surviving blob would render as a 403 broken image. Re-signed per read, as the
    /// live board does, but not written back: the row is a record, and the output cache already
    /// stops this costing a signature per view.
    /// </summary>
    private static async Task<string> FreshReadUrlAsync(IBlobStorageService blobStorageService, ILogger logger, string url)
    {
        if (!SasUrl.IsExpiringSoon(url))
        {
            return url;
        }

        try
        {
            return await blobStorageService.RefreshSasUrlAsync(url);
        }
        catch (Exception ex) when (ex is RequestFailedException or UriFormatException or InvalidOperationException)
        {
            logger.LogDebug(ex, "Could not re-sign archived comic artwork");
            return url;
        }
    }

    /// <summary>
    /// Whether an archived comic's artwork is still there, so the client can render a score
    /// card instead of a broken image. Only blobs this app uploaded can be probed; anything
    /// else (including seeded test URLs) is assumed present.
    /// </summary>
    private static async Task<bool> IsImageGoneAsync(
        IBlobStorageService blobStorageService,
        ILogger logger,
        string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return true;
        }

        try
        {
            if (!new Uri(url).AbsolutePath.Contains("/comics/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return !await blobStorageService.BlobExistsAsync(url);
        }
        catch (Exception ex) when (ex is UriFormatException or RequestFailedException)
        {
            // An unprobeable URL is not evidence of deletion; assume the image is fine and let
            // the browser be the judge.
            logger.LogDebug(ex, "Could not probe archived comic artwork");
            return false;
        }
    }

    private static async Task<IResult> GetLeaderboard(
        ILeaderboardService leaderboardService,
        ILogger<ILeaderboardService> logger,
        int limit = 10,
        string? region = null,
        string? category = null)
    {
        if (!PlaceCategories.TryParse(category, out var kind))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid category",
                detail: "Category must be restaurant or apartment.");
        }
        // No region is now the worldwide board — the page has no picker to send one. A scoped
        // board stays available by URL (?region=US). RegionCode.From maps empty to US, which is
        // exactly the wrong default here, so the emptiness is resolved before it reaches the id.
        var isGlobal = string.IsNullOrWhiteSpace(region);

        if (!isGlobal && !RegionFormat.IsMatch(region!))
        {
            logger.LogWarning("GetLeaderboard called with invalid region format: {Region}", region);
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid region",
                detail: $"Region '{region}' has invalid format. Must start with a 2-letter country code (e.g., US, GB, US-WA).");
        }

        if (limit < 1 || limit > 50)
        {
            logger.LogWarning("GetLeaderboard called with invalid limit: {Limit}", limit);
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid limit", detail: "Limit must be between 1 and 50");
        }

        try
        {
            logger.LogInformation("Fetching leaderboard for {Scope} with limit {Limit}",
                isGlobal ? "all regions" : $"region {region}", limit);

            var regionCode = isGlobal ? new RegionCode(string.Empty) : RegionCode.From(region);
            var entries = await leaderboardService.GetTopComicsAsync(regionCode, limit, kind);

            var response = new LeaderboardResponse
            {
                Region = isGlobal ? "ALL" : region!.ToUpperInvariant(),
                Category = kind.ToWire(),
                Entries = entries.Select(e => new LeaderboardEntryDto
                {
                    Rank = e.Rank,
                    PlaceId = e.PlaceId.Value,
                    RestaurantName = e.RestaurantName,
                    Address = e.Address,
                    Region = e.Region.Value,
                    StrangenessScore = e.StrangenessScore,
                    ComicBlobUrl = e.ComicBlobUrl,
                    LastUpdated = e.LastUpdated,
                    Latitude = e.Latitude,
                    Longitude = e.Longitude
                }).ToList(),
                LastUpdated = DateTimeOffset.UtcNow
            };

            logger.LogInformation("Retrieved {Count} leaderboard entries for region {Region}", entries.Count, region);
            return Results.Ok(response);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving leaderboard for region {Region}", region);
            return Results.Problem(statusCode: StatusCodes.Status500InternalServerError,
                title: "Internal server error", detail: "An error occurred while retrieving the leaderboard");
        }
    }
}
