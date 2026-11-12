using FluentValidation;
using Microsoft.ApplicationInsights;
using PoSeeReview.Api.Identity;
using PoSeeReview.Api.Telemetry;
using PoSeeReview.Shared.Contracts;
using PoSeeReview.Shared.Dtos;
using PoSeeReview.Shared.Ids;

namespace PoSeeReview.Api.Features.Moderation;

/// <summary>
/// Public content-reporting slice. Maps <c>/api/reports</c> (NET_RULES 3.3).
/// <para>
/// This exists because the app publishes AI-generated content about real, named businesses and
/// had no path for a person to flag any of it. The Takedowns slice is not that path: it carries
/// a shared admin key and deletes the comic, its blob and its leaderboard row on the spot —
/// correct for a verified legal request, and an anonymous deletion API if it were opened up.
/// </para>
/// <para>
/// So this endpoint requires a session (the deny-by-default fallback policy gives it one for
/// free), is rate limited, dedupes by reporter, and only ever writes a row. Nothing here
/// deletes anything.
/// </para>
/// </summary>
internal static class ReportsEndpoints
{
    /// <summary>Named limiter for report submission.</summary>
    public const string RateLimitPolicy = "reports-post";

    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/reports").WithTags("Reports");

        group.MapPost("", SubmitAsync).RequireRateLimiting(RateLimitPolicy);

        return app;
    }

    internal static async Task<IResult> SubmitAsync(
        ComicReportRequestDto request,
        IValidator<ComicReportRequestDto> validator,
        ComicReportRepository repository,
        IContentModerationGate moderationGate,
        ICurrentRequestIdentityAccessor identityAccessor,
        TimeProvider timeProvider,
        ILogger<ComicReportRequestDto> logger,
        TelemetryClient telemetryClient,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return Results.ValidationProblem(validation.ToDictionary());
        }

        var placeId = PlaceId.From(request.PlaceId);
        var userId = identityAccessor.GetCurrentUserId();
        var reportId = Guid.NewGuid().ToString("N");

        var entity = new ComicReportEntity
        {
            PartitionKey = ComicReportEntity.PartitionKeyFor(placeId),
            RowKey = ComicReportEntity.RowKeyFor(userId),
            ReportId = reportId,
            PlaceId = placeId.Value,
            Reason = request.Reason.ToString(),
            Details = request.Details,
            ContactEmail = request.ContactEmail,
            ReportedAt = timeProvider.GetUtcNow()
        };

        var recorded = await repository.TryAddAsync(entity, cancellationToken);

        // Reason and place are safe to record; the reporter's contact address and free text are
        // not (NET_RULES 5.1/6.1). The row keeps them, the log and telemetry never see them.
        logger.LogInformation(
            "Comic report {Recorded} for {PlaceId} with reason {Reason}",
            recorded ? "recorded" : "deduplicated", placeId, request.Reason);

        if (recorded)
        {
            // A report that only ever writes a row is a form that says "we look at these daily"
            // and then does not. Feeding the count to the moderation gate is what turns enough
            // independent reports into an automatic hide, pending a human.
            var distinctReporters = await repository.CountForPlaceAsync(placeId, cancellationToken);
            var autoHidden = await moderationGate.RecordReportAsync(placeId, distinctReporters, cancellationToken);

            if (autoHidden)
            {
                logger.LogWarning("Comic {PlaceId} was auto-hidden after {Count} reports", placeId, distinctReporters);
                telemetryClient.TrackEvent("ComicAutoHidden", new Dictionary<string, string>
                {
                    ["PlaceId"] = placeId.Value,
                    ["ReportCount"] = distinctReporters.ToString(System.Globalization.CultureInfo.InvariantCulture)
                });
            }

            PoSeeReviewTelemetry.ComicReports.Add(1,
                [new KeyValuePair<string, object?>("reason", request.Reason.ToString())]);

            telemetryClient.TrackEvent("ComicReportReceived", new Dictionary<string, string>
            {
                ["PlaceId"] = placeId.Value,
                ["Reason"] = request.Reason.ToString()
            });
        }

        // 202 either way. Telling someone their report "failed" because they had already sent
        // one reads as the app ignoring them, which is the opposite of what a reporting flow
        // is for.
        return Results.Accepted(value: new ComicReportResponseDto
        {
            ReportId = recorded ? reportId : string.Empty,
            AlreadyReported = !recorded,
            Message = recorded
                ? "Thanks — this comic has been flagged for review. We look at reports daily and remove anything that breaks the rules."
                : "You have already reported this comic. It is in the queue and we will not lose it."
        });
    }
}
