using PoSeeReview.Shared.Dtos;
using PoSeeReview.Shared.Enums;

namespace PoSeeReview.Client.Services;

/// <summary>
/// The last discovery search and what it found, so coming Back from a comic restores the grid
/// instead of re-running the search (or re-asking for geolocation).
/// </summary>
public sealed class DiscoveryState
{
    /// <summary>The text searched, or null for a location search.</summary>
    public string? Query { get; private set; }

    public List<RestaurantDto> Restaurants { get; private set; } = [];

    /// <summary>Restaurants or apartments — what the results are, and what the comic page goes Back to.</summary>
    public PlaceCategory Category { get; private set; } = PlaceCategory.Restaurant;

    /// <summary>Where a nearby search was centred, so switching category can repeat it; null for a text search.</summary>
    public (double Lat, double Lng)? Center { get; private set; }

    /// <summary>Raised by <see cref="Clear"/>, so a discovery page already on screen drops its results too.</summary>
    public event Action? Cleared;

    public void Set(string? query, List<RestaurantDto> restaurants, PlaceCategory category = PlaceCategory.Restaurant, (double Lat, double Lng)? center = null) =>
        (Query, Restaurants, Category, Center) = (query, restaurants, category, center);

    /// <summary>Back to the landing — what the brand link does. The category is a preference, so it stays.</summary>
    public void Clear()
    {
        Set(null, [], Category);
        Cleared?.Invoke();
    }
}
