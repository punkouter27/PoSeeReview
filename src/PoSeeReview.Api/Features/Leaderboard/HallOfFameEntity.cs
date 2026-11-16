using System.Globalization;
using Azure;
using Azure.Data.Tables;
using PoSeeReview.Shared.Contracts;
using PoSeeReview.Shared.Enums;
using PoSeeReview.Shared.Ids;

namespace PoSeeReview.Api.Features.Leaderboard;

/// <summary>
/// A comic promoted into the permanent weekly archive.
/// <para>
/// Comics expire after 24 hours and the live board churns with them. That is the right product
/// decision and it also means nothing accumulates — there is no reason to come back on Tuesday.
/// These rows are written as scores are recorded and are never touched by
/// <c>ExpiredComicCleanupService</c>, so the record that a place once scored 97 outlives the
/// comic that earned it.
/// </para>
/// <para>
/// PartitionKey: <c>HOF_{Region}_{WeekKey}</c> — one partition per region-week, so rendering a
/// week is a single partition query.
/// RowKey: <c>{InvertedScore}_{PlaceId}</c>, the same inversion the live board uses for
/// zero-cost descending sort.
/// </para>
/// </summary>
public class HallOfFameEntity : ITableEntity
{
    private const string PartitionKeyPrefix = "HOF";

    /// <summary>
    /// ISO-8601 week key for an instant, e.g. <c>2026-W36</c>.
    /// <para>
    /// Uses <see cref="ISOWeek"/> rather than <see cref="Calendar.GetWeekOfYear"/>: the latter
    /// needs a rule and a first-day-of-week argument, and getting either wrong silently files
    /// entries into a neighbouring week at the year boundary. <see cref="ISOWeek"/> also gives
    /// the matching year, which is what makes late-December weeks sort correctly.
    /// </para>
    /// </summary>
    public static string WeekKeyFor(DateTimeOffset instant)
    {
        var date = instant.UtcDateTime;
        return $"{ISOWeek.GetYear(date)}-W{ISOWeek.GetWeekOfYear(date):D2}";
    }

    /// <summary>UTC Monday that starts the week named by <paramref name="weekKey"/>.</summary>
    public static DateTimeOffset WeekStartFor(string weekKey)
    {
        var parts = weekKey.Split("-W");
        if (parts.Length == 2
            && int.TryParse(parts[0], out var year)
            && int.TryParse(parts[1], out var week))
        {
            return new DateTimeOffset(ISOWeek.ToDateTime(year, week, DayOfWeek.Monday), TimeSpan.Zero);
        }

        return DateTimeOffset.MinValue;
    }

    /// <summary>Builds the partition key for one region-week.</summary>
    public static string PartitionKeyFor(RegionCode region, string weekKey) =>
        $"{PartitionKeyPrefix}_{region.Value}_{weekKey}";

    public string PartitionKey { get; set; } = string.Empty;
    public string RowKey { get; set; } = string.Empty;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string PlaceId { get; set; } = string.Empty;
    public string RestaurantName { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string WeekKey { get; set; } = string.Empty;
    public double StrangenessScore { get; set; }

    /// <summary>
    /// The comic image as it stood when archived. Blobs carry an 8-day SAS and are cleaned up
    /// after expiry, so this URL is expected to stop resolving; the row survives regardless.
    /// </summary>
    public string ComicBlobUrl { get; set; } = string.Empty;

    public DateTimeOffset ArchivedAt { get; set; }

    /// <summary>"restaurant" or "apartment"; rows archived before apartments have none and read as restaurant.</summary>
    public string? Category { get; set; }

    /// <summary>The archive this row belongs to.</summary>
    public PlaceCategory CategoryValue => PlaceCategories.FromWire(Category);

    /// <summary>Projects a live leaderboard entry into an archive row for the given week.</summary>
    public static HallOfFameEntity FromEntry(LeaderboardEntry entry, string weekKey, DateTimeOffset archivedAt)
    {
        var invertedScore = 9999999999 - (long)Math.Floor(entry.StrangenessScore * 100000000.0);

        return new HallOfFameEntity
        {
            PartitionKey = PartitionKeyFor(entry.Region, weekKey),
            RowKey = $"{invertedScore:D10}_{entry.PlaceId.Value}",
            PlaceId = entry.PlaceId.Value,
            RestaurantName = entry.RestaurantName,
            Address = entry.Address,
            Region = entry.Region.Value,
            WeekKey = weekKey,
            StrangenessScore = entry.StrangenessScore,
            ComicBlobUrl = entry.ComicBlobUrl,
            ArchivedAt = archivedAt,
            Category = entry.Category.ToWire()
        };
    }
}
