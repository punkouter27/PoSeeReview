using Azure.Data.Tables;
using Azure;
using PoSeeReview.Shared.Contracts;
using PoSeeReview.Shared.Ids;
using PoSeeReview.Shared.Enums;

namespace PoSeeReview.Api.Features.Restaurants;

/// <summary>
/// Azure Table Storage entity for restaurant data with 24-hour cache
/// Partition Key: RESTAURANT_{Region}, Row Key: {PlaceId}
/// </summary>
public class RestaurantEntity : ITableEntity
{
    // Azure Table required properties
    public string PartitionKey { get; set; } = string.Empty;
    public string RowKey { get; set; } = string.Empty;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    // Business properties
    public string PlaceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string Region { get; set; } = string.Empty;
    public double AverageRating { get; set; }
    public int TotalReviews { get; set; }

    /// <summary>
    /// Serialized JSON of List&lt;Review&gt; (max 32KB for Azure Table property limit)
    /// Stores top 10 reviews only
    /// </summary>
    public string ReviewsJson { get; set; } = string.Empty;

    /// <summary>
    /// Cache timestamp for 24-hour expiration strategy
    /// </summary>
    public DateTimeOffset CachedAt { get; set; }

    /// <summary>"restaurant" or "apartment"; absent on rows cached before apartments, which read as restaurant.</summary>
    public string? Category { get; set; }

    /// <summary>Prefix for the per-region partition key.</summary>
    public const string PartitionKeyPrefix = "RESTAURANT";

    /// <summary>
    /// Creates the partition key holding every restaurant in <paramref name="region"/>
    /// (format: RESTAURANT_{Region}).
    /// </summary>
    public static string CreatePartitionKey(RegionCode region)
        => $"{PartitionKeyPrefix}_{region.Value}";

    /// <summary>
    /// Creates the row key from a Google place identifier.
    /// </summary>
    public static string CreateRowKey(PlaceId placeId)
        => placeId.Value;
}
