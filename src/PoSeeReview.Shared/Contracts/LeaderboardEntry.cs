using PoSeeReview.Shared.Enums;
using PoSeeReview.Shared.Ids;

namespace PoSeeReview.Shared.Contracts;

/// <summary>
/// A leaderboard row tracking the highest strangeness score per restaurant per region.
/// Consumed by the Leaderboard, Comics and Takedowns slices, so it lives in Shared
/// (NET_RULES 2.2).
/// </summary>
public class LeaderboardEntry
{
    /// <summary>
    /// 1-based ranking position in leaderboard
    /// </summary>
    public int Rank { get; set; }

    /// <summary>
    /// Google Maps place identifier
    /// </summary>
    public PlaceId PlaceId { get; set; } = PlaceId.Empty;

    /// <summary>
    /// Restaurant display name
    /// </summary>
    public string RestaurantName { get; set; } = string.Empty;

    /// <summary>
    /// Full address of the restaurant
    /// </summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// Geographic region code (e.g. US-WA-Seattle)
    /// </summary>
    public RegionCode Region { get; set; } = RegionCode.Default;

    /// <summary>
    /// Highest strangeness score achieved by any comic for this restaurant (0-100)
    /// </summary>
    public double StrangenessScore { get; set; }

    /// <summary>
    /// URL to the comic image in Azure Blob Storage (thumbnail for leaderboard display)
    /// </summary>
    public string ComicBlobUrl { get; set; } = string.Empty;

    /// <summary>
    /// Last time this leaderboard entry was updated
    /// </summary>
    public DateTimeOffset LastUpdated { get; set; }

    /// <summary>Where the restaurant is, for the board's map. Null on rows written before it was stored.</summary>
    public double? Latitude { get; set; }

    /// <inheritdoc cref="Latitude"/>
    public double? Longitude { get; set; }

    /// <summary>Which board the row belongs to; restaurants and apartments rank separately.</summary>
    public PlaceCategory Category { get; set; } = PlaceCategory.Restaurant;
}
