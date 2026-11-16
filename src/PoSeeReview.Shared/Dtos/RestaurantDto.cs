namespace PoSeeReview.Shared.Dtos;

/// <summary>
/// DTO for restaurant summary information (GET /api/restaurants/nearby)
/// </summary>
public class RestaurantDto
{
    /// <summary>
    /// Google Maps place ID (unique identifier)
    /// </summary>
    public string PlaceId { get; set; } = string.Empty;

    /// <summary>
    /// Restaurant name (max 255 chars)
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
    /// Google average rating (0-5 stars)
    /// </summary>
    public double AverageRating { get; set; }

    /// <summary>
    /// Total number of reviews on Google
    /// </summary>
    public int TotalReviews { get; set; }

    /// <summary>
    /// Region code (e.g., "US", "US-WA-Seattle")
    /// </summary>
    public string Region { get; set; } = "US";

    /// <summary>
    /// Distance from user in kilometers (calculated from search coordinates)
    /// </summary>
    public double Distance { get; set; }

    /// <summary>"restaurant" or "apartment".</summary>
    public string Category { get; set; } = "restaurant";
}
