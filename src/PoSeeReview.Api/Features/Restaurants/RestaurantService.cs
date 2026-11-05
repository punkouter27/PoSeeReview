using Microsoft.Extensions.Logging;
using PoSeeReview.Shared.Contracts;
using PoSeeReview.Shared.Ids;
using PoSeeReview.Shared.Enums;

namespace PoSeeReview.Api.Features.Restaurants;

/// <summary>
/// Service for discovering and managing restaurant data with 24-hour caching
/// </summary>
public class RestaurantService : IRestaurantService
{
    private readonly RestaurantRepository _repository;
    private readonly GoogleMapsService _googleMapsService;
    private readonly ILogger<RestaurantService> _logger;

    public RestaurantService(
        RestaurantRepository repository,
        GoogleMapsService googleMapsService,
        ILogger<RestaurantService> logger)
    {
        _repository = repository;
        _googleMapsService = googleMapsService;
        _logger = logger;
    }

    /// <summary>
    /// Finds nearby restaurants within 5km radius using Google Maps API
    /// </summary>
    public async Task<List<Restaurant>> GetNearbyRestaurantsAsync(
        double latitude,
        double longitude,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        if (!_googleMapsService.ValidateCoordinates(latitude, longitude))
        {
            throw new ArgumentException("Invalid coordinates: latitude must be -90 to 90, longitude must be -180 to 180");
        }

        if (limit < 1 || limit > 50)
        {
            throw new ArgumentException("Limit must be between 1 and 50");
        }

        _logger.LogInformation(
            "Getting nearby restaurants at ({Latitude}, {Longitude}), limit {Limit}",
            latitude, longitude, limit);

        // Not written to the restaurant cache: the cache is only ever read for a place's
        // reviews, a search result has none, and upserting it wiped the reviews a comic had
        // already paid Place Details for.
        var restaurants = await _googleMapsService.SearchNearbyAsync(latitude, longitude, cancellationToken: cancellationToken);

        // Return top N by distance
        return restaurants
            .OrderBy(r => GeoUtils.CalculateDistance(latitude, longitude, r.Latitude, r.Longitude))
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// Gets detailed restaurant information by Google place ID
    /// Returns cached data if available (24-hour TTL), otherwise fetches from Google Maps
    /// </summary>
    public async Task<Restaurant> GetRestaurantByPlaceIdAsync(PlaceId placeId, CancellationToken cancellationToken = default)
    {
        if (placeId.IsEmpty)
        {
            throw new ArgumentException("PlaceId is required", nameof(placeId));
        }

        _logger.LogInformation("Getting restaurant details for {PlaceId}", placeId);

        // Try cache first (requires region - simplified, use wildcard query)
        var cachedRestaurant = await TryGetFromCacheAsync(placeId);

        if (cachedRestaurant != null && cachedRestaurant.Reviews?.Count > 0)
        {
            _logger.LogInformation("Cache hit for restaurant {PlaceId} with {ReviewCount} reviews", placeId, cachedRestaurant.Reviews.Count);
            return cachedRestaurant;
        }

        _logger.LogInformation("Cache miss or no reviews for restaurant {PlaceId}, fetching from Google Maps", placeId);

        // Fetch from Google Maps with reviews
        var restaurant = await _googleMapsService.GetPlaceDetailsAsync(placeId, cancellationToken);

        if (restaurant == null)
        {
            throw new KeyNotFoundException($"Restaurant with placeId '{placeId}' not found");
        }

        // Cache the restaurant with reviews
        await _repository.UpsertRestaurantAsync(restaurant);
        _logger.LogInformation("Cached restaurant {PlaceId} with {ReviewCount} reviews", placeId, restaurant.Reviews?.Count ?? 0);

        return restaurant;
    }

    /// <summary>Not cached: only the audio comic asks, once per comic, and it caches its own result.</summary>
    public async Task<IReadOnlyList<Review>> GetNewestReviewsAsync(PlaceId placeId, CancellationToken cancellationToken = default) =>
        await _googleMapsService.GetNewestReviewsAsync(placeId, cancellationToken);

    public async Task<(double lat, double lon)?> GeocodeLocationAsync(string locationQuery, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(locationQuery))
        {
            return null;
        }

        return await _googleMapsService.GeocodeLocationAsync(locationQuery, cancellationToken);
    }

    /// <summary>
    /// Attempts to get restaurant from cache by probing each region partition it could have been
    /// stored under. Restaurants are written with a partition key of RESTAURANT_{region}, where
    /// region is the ISO 3166-1 alpha-2 country code produced by GoogleMapsService.DetermineRegion.
    /// Point-lookups by (region, placeId) across <see cref="RegionCode.KnownCountries"/> make the
    /// 24h cache genuinely hit; the previous hardcoded city strings never matched a stored partition.
    /// </summary>
    private async Task<Restaurant?> TryGetFromCacheAsync(PlaceId placeId)
    {
        foreach (var region in RegionCode.KnownCountries)
        {
            var restaurant = await _repository.GetByPlaceIdAsync(placeId, region);
            if (restaurant != null)
            {
                return restaurant;
            }
        }

        return null;
    }
}
