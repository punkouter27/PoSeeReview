using PoSeeReview.Shared.Ids;

namespace PoSeeReview.Shared.Contracts;

/// <summary>
/// Discovers and caches restaurant data (24-hour TTL). Declared in Shared because the Comics
/// slice orchestrates against it without reaching into the Restaurants slice (NET_RULES 2.2).
/// </summary>
public interface IRestaurantService
{
    /// <summary>
    /// Finds nearby restaurants within 5km radius using Google Maps API.
    /// Returns cached results if available (24-hour TTL).
    /// </summary>
    /// <param name="latitude">User latitude (-90 to 90)</param>
    /// <param name="longitude">User longitude (-180 to 180)</param>
    /// <param name="limit">Maximum results to return (default 10)</param>
    /// <param name="cancellationToken">Cancels the lookup</param>
    /// <returns>List of restaurants with distance from user</returns>
    /// <exception cref="ArgumentException">Invalid coordinates</exception>
    /// <exception cref="HttpRequestException">Google Maps API failure</exception>
    Task<List<Restaurant>> GetNearbyRestaurantsAsync(double latitude, double longitude, int limit = 10, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets detailed restaurant information by place identifier.
    /// Returns cached data if available (24-hour TTL), otherwise fetches from Google Maps.
    /// </summary>
    /// <param name="placeId">Google Maps place identifier</param>
    /// <param name="cancellationToken">Cancellation token for async operations</param>
    /// <returns>Restaurant with reviews and strangeness scores</returns>
    /// <exception cref="ArgumentNullException">placeId is empty</exception>
    /// <exception cref="KeyNotFoundException">Restaurant not found</exception>
    Task<Restaurant> GetRestaurantByPlaceIdAsync(PlaceId placeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Up to five of the place's newest reviews, beyond the "most relevant" five on
    /// <see cref="GetRestaurantByPlaceIdAsync"/>. Uncached and not part of the comic's score; the
    /// audio comic reads it for more source material. Empty where no implementation offers it.
    /// </summary>
    Task<IReadOnlyList<Review>> GetNewestReviewsAsync(PlaceId placeId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Review>>([]);

    /// <summary>
    /// Geocodes a free-form location query (city, ZIP/postal code, etc.) to coordinates.
    /// </summary>
    /// <param name="locationQuery">User-entered location text</param>
    /// <param name="cancellationToken">Cancellation token for async operations</param>
    /// <returns>Latitude/longitude when geocoding succeeds; otherwise null</returns>
    Task<(double lat, double lon)?> GeocodeLocationAsync(string locationQuery, CancellationToken cancellationToken = default);
}
