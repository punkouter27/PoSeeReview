using PoSeeReview.Shared.Enums;
using PoSeeReview.Shared.Ids;

namespace PoSeeReview.Shared.Contracts;

/// <summary>
/// A restaurant discovered via Google Maps with cached metadata and reviews. Consumed by the
/// Restaurants, Comics and Leaderboard slices, so it lives in Shared (NET_RULES 2.2).
/// </summary>
public class Restaurant
{
    /// <summary>
    /// Google Maps place identifier
    /// </summary>
    public PlaceId PlaceId { get; set; } = PlaceId.Empty;

    /// <summary>
    /// Restaurant name
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Full street address
    /// </summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// Latitude coordinate (-90 to 90)
    /// </summary>
    public double Latitude { get; set; }

    /// <summary>
    /// Longitude coordinate (-180 to 180)
    /// </summary>
    public double Longitude { get; set; }

    /// <summary>
    /// Region used for table partitioning (format: {Country}-{State}-{City})
    /// </summary>
    public RegionCode Region { get; set; } = RegionCode.Default;

    /// <summary>
    /// Average rating from Google (0-5 stars)
    /// </summary>
    public double AverageRating { get; set; }

    /// <summary>
    /// Total number of reviews on Google
    /// </summary>
    public int TotalReviews { get; set; }

    /// <summary>
    /// Top 10 reviews from Google Maps (cached)
    /// </summary>
    public List<Review> Reviews { get; set; } = new();

    /// <summary>
    /// Timestamp when data was cached (for 24-hour expiration)
    /// </summary>
    public DateTimeOffset CachedAt { get; set; }

    /// <summary>Restaurant or apartment, from the Google place types (or the search that found it).</summary>
    public PlaceCategory Category { get; set; } = PlaceCategory.Restaurant;
}
