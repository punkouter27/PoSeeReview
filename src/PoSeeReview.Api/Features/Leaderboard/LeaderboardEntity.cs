using Azure.Data.Tables;
using Azure;
using PoSeeReview.Shared.Contracts;
using PoSeeReview.Shared.Ids;
// The entity exposes string columns named PlaceId/Region, which would shadow the id types
// inside ToDomain(); the aliases keep both readable.
using PlaceIdentifier = PoSeeReview.Shared.Ids.PlaceId;
using RegionIdentifier = PoSeeReview.Shared.Ids.RegionCode;
using PoSeeReview.Shared.Enums;

namespace PoSeeReview.Api.Features.Leaderboard;

/// <summary>
/// Azure Table Storage entity for leaderboard with inverted RowKey for descending sort
/// PartitionKey: LEADERBOARD_{Region}
/// RowKey: {InvertedScore}_{PlaceId} (inverted for descending sort by score)
/// </summary>
public class LeaderboardEntity : ITableEntity
{
    /// <summary>Prefix for the per-region partition key.</summary>
    public const string PartitionKeyPrefix = "LEADERBOARD";

    /// <summary>Builds the partition key holding every row for <paramref name="region"/>.</summary>
    public static string PartitionKeyFor(RegionIdentifier region) => $"{PartitionKeyPrefix}_{region.Value}";

    /// <summary>
    /// Partition key format: LEADERBOARD_{Region}
    /// Example: LEADERBOARD_US-WA-Seattle
    /// </summary>
    public string PartitionKey { get; set; } = string.Empty;

    /// <summary>
    /// Row key format: {InvertedScore}_{PlaceId}
    /// InvertedScore = 9999999999 - (score * 100000000) for descending sort
    /// Example: 0000000500_ChIJabc123 (for score 95.00)
    /// </summary>
    public string RowKey { get; set; } = string.Empty;

    /// <summary>
    /// Azure Table Storage timestamp
    /// </summary>
    public DateTimeOffset? Timestamp { get; set; }

    /// <summary>
    /// Azure Table Storage ETag for optimistic concurrency
    /// </summary>
    public ETag ETag { get; set; }

    // Business properties

    /// <summary>
    /// Google Maps Place ID
    /// </summary>
    public string PlaceId { get; set; } = string.Empty;

    /// <summary>
    /// Restaurant name
    /// </summary>
    public string RestaurantName { get; set; } = string.Empty;

    /// <summary>
    /// Full address
    /// </summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// Geographic region code (e.g., US-WA-Seattle)
    /// </summary>
    public string Region { get; set; } = string.Empty;

    /// <summary>
    /// Strangeness score (0-100)
    /// </summary>
    public double StrangenessScore { get; set; }

    /// <summary>
    /// URL to comic image in Azure Blob Storage
    /// </summary>
    public string ComicBlobUrl { get; set; } = string.Empty;

    /// <summary>
    /// Last update timestamp
    /// </summary>
    public DateTimeOffset LastUpdated { get; set; }

    /// <summary>Restaurant coordinates for the map. Absent on older rows, which read back as null.</summary>
    public double? Latitude { get; set; }

    /// <inheritdoc cref="Latitude"/>
    public double? Longitude { get; set; }

    /// <summary>"restaurant" or "apartment"; rows written before apartments have none and read as restaurant.</summary>
    public string? Category { get; set; }

    /// <summary>The board this row ranks on.</summary>
    public PlaceCategory CategoryValue => PlaceCategories.FromWire(Category);

    /// <summary>
    /// Converts domain LeaderboardEntry to Table Storage entity
    /// </summary>
    public static LeaderboardEntity FromDomain(LeaderboardEntry entry)
    {
        var invertedScore = CalculateInvertedScore(entry.StrangenessScore);

        return new LeaderboardEntity
        {
            PartitionKey = PartitionKeyFor(entry.Region),
            RowKey = $"{invertedScore:D10}_{entry.PlaceId.Value}",
            PlaceId = entry.PlaceId.Value,
            RestaurantName = entry.RestaurantName,
            Address = entry.Address,
            Region = entry.Region.Value,
            StrangenessScore = entry.StrangenessScore,
            ComicBlobUrl = entry.ComicBlobUrl,
            LastUpdated = entry.LastUpdated,
            Latitude = entry.Latitude,
            Longitude = entry.Longitude,
            Category = entry.Category.ToWire()
        };
    }

    /// <summary>
    /// Converts Table Storage entity to domain LeaderboardEntry
    /// </summary>
    public LeaderboardEntry ToDomain(int rank)
    {
        return new LeaderboardEntry
        {
            Rank = rank,
            PlaceId = PlaceIdentifier.From(PlaceId),
            RestaurantName = RestaurantName,
            Address = Address,
            Region = RegionIdentifier.From(Region),
            StrangenessScore = StrangenessScore,
            ComicBlobUrl = ComicBlobUrl,
            LastUpdated = LastUpdated,
            Latitude = Latitude,
            Longitude = Longitude,
            Category = CategoryValue
        };
    }

    /// <summary>
    /// Calculates inverted score for RowKey to enable descending sort
    /// Score is scaled by 100000000 to handle decimal precision
    /// Returns: 9999999999 - (score * 100000000)
    /// </summary>
    private static long CalculateInvertedScore(double score)
    {
        var scaledScore = (long)Math.Floor(score * 100000000.0);
        return 9999999999 - scaledScore;
    }
}
