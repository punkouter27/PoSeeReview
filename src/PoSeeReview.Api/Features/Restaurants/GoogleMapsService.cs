using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PoSeeReview.Shared.Contracts;
using PoSeeReview.Shared.Ids;
using PoSeeReview.Shared.Enums;

namespace PoSeeReview.Api.Features.Restaurants;

/// <summary>
/// Google Maps API integration service for restaurant discovery
/// </summary>
public partial class GoogleMapsService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<GoogleMapsService> _logger;
    private readonly string _apiKey;

    public GoogleMapsService(
        HttpClient httpClient,
        ILogger<GoogleMapsService> logger,
        IConfiguration configuration)
    {
        _httpClient = httpClient;
        _logger = logger;
        _apiKey = configuration["GoogleMaps:ApiKey"] ?? throw new InvalidOperationException("GoogleMaps:ApiKey not configured");
    }

    /// <summary>
    /// Validates geographic coordinates
    /// </summary>
    public bool ValidateCoordinates(double latitude, double longitude)
    {
        if (latitude < -90 || latitude > 90)
        {
            _logger.LogWarning("Invalid latitude: {Latitude}", latitude);
            return false;
        }

        if (longitude < -180 || longitude > 180)
        {
            _logger.LogWarning("Invalid longitude: {Longitude}", longitude);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Geocodes a free-form location query (city, ZIP/postal code, etc.) into coordinates.
    /// </summary>
    public async Task<(double lat, double lon)?> GeocodeLocationAsync(
        string locationQuery,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(locationQuery))
        {
            return null;
        }

        var encoded = Uri.EscapeDataString(locationQuery.Trim());
        var url = $"https://maps.googleapis.com/maps/api/geocode/json?address={encoded}&key={_apiKey}";

        _logger.LogInformation("Geocoding location query: {LocationQuery}", locationQuery);

        var response = await _httpClient.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning("Google Geocoding API returned {StatusCode} for '{LocationQuery}': {ErrorContent}",
                response.StatusCode,
                locationQuery,
                errorContent);
            return null;
        }

        var geocode = await response.Content.ReadFromJsonAsync<GeocodeResponse>(cancellationToken: cancellationToken);
        if (geocode == null || !string.Equals(geocode.Status, "OK", StringComparison.OrdinalIgnoreCase) || geocode.Results == null || geocode.Results.Count == 0)
        {
            _logger.LogInformation("No geocode results for location query: {LocationQuery}. Status: {Status}", locationQuery, geocode?.Status);
            return await TryResolveLocationWithPlacesTextSearchAsync(locationQuery, cancellationToken);
        }

        var first = geocode.Results[0].Geometry?.Location;
        if (first == null || !ValidateCoordinates(first.Latitude, first.Longitude))
        {
            return null;
        }

        return (first.Latitude, first.Longitude);
    }

    private async Task<(double lat, double lon)?> TryResolveLocationWithPlacesTextSearchAsync(
        string locationQuery,
        CancellationToken cancellationToken)
    {
        var requestBody = new
        {
            textQuery = locationQuery,
            maxResultCount = 1
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "https://places.googleapis.com/v1/places:searchText")
        {
            Content = JsonContent.Create(requestBody)
        };
        request.Headers.Add("X-Goog-Api-Key", _apiKey);
        request.Headers.Add("X-Goog-FieldMask", "places.location");

        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning("Google Places Text Search returned {StatusCode} for '{LocationQuery}': {ErrorContent}",
                response.StatusCode,
                locationQuery,
                errorContent);
            return null;
        }

        var textSearch = await response.Content.ReadFromJsonAsync<GooglePlacesResponse>(cancellationToken: cancellationToken);
        var candidate = textSearch?.Places?.FirstOrDefault()?.Location;
        if (candidate == null || !ValidateCoordinates(candidate.Latitude, candidate.Longitude))
        {
            return null;
        }

        _logger.LogInformation("Resolved location query '{LocationQuery}' via Places Text Search fallback", locationQuery);
        return (candidate.Latitude, candidate.Longitude);
    }

    /// <summary>
    /// Searches for nearby restaurants using Google Places API (New)
    /// </summary>
    /// <param name="latitude">Search center latitude</param>
    /// <param name="longitude">Search center longitude</param>
    /// <param name="radiusMeters">Search radius in meters (default 5000m = 5km)</param>
    /// <param name="cancellationToken">Cancels the upstream Places call</param>
    /// <returns>List of restaurants with basic metadata</returns>
    public async Task<List<Restaurant>> SearchNearbyAsync(
        double latitude,
        double longitude,
        int radiusMeters = 5000,
        CancellationToken cancellationToken = default)
    {
        if (!ValidateCoordinates(latitude, longitude))
        {
            throw new ArgumentException("Invalid coordinates");
        }

        _logger.LogInformation(
            "Searching nearby restaurants at ({Latitude}, {Longitude}) within {Radius}m",
            latitude, longitude, radiusMeters);

        // Google Places API (New) endpoint - requires API key in header, not query string
        var requestBody = new
        {
            includedTypes = new[] { "restaurant" },
            maxResultCount = 20,
            // DISTANCE, not the API's POPULARITY default.
            //
            // searchNearby ranks by prominence unless told otherwise, so within a 5km radius it
            // returns the twenty most famous places — chains and landmarks with thousands of
            // ratings. Sorting those by distance afterwards (which the endpoint does) cannot undo
            // the selection, so every result came back 3.8-4.8 stars with 1000+ reviews.
            //
            // That fights the product on two fronts: the PRD asks for proximity-first serendipity
            // rather than a "best of" list, and the comic pipeline mines ONE-STAR reviews — the
            // places most likely to have interesting ones are precisely the ones prominence
            // ranking filters out.
            rankPreference = "DISTANCE",
            locationRestriction = new
            {
                circle = new
                {
                    center = new
                    {
                        latitude,
                        longitude
                    },
                    radius = radiusMeters
                }
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "https://places.googleapis.com/v1/places:searchNearby")
        {
            Content = JsonContent.Create(requestBody)
        };
        request.Headers.Add("X-Goog-Api-Key", _apiKey);
        request.Headers.Add("X-Goog-FieldMask", "places.id,places.displayName,places.formattedAddress,places.location,places.rating,places.userRatingCount");

        var response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Google Places API error: {StatusCode} - {ErrorContent}", response.StatusCode, errorContent);
            // Include the actual Google API error body so callers can surface it for debugging
            throw new HttpRequestException(
                $"Google Places API error {(int)response.StatusCode}: {errorContent}",
                inner: null,
                statusCode: response.StatusCode);
        }

        var result = await response.Content.ReadFromJsonAsync<GooglePlacesResponse>(cancellationToken: cancellationToken);

        return result?.Places?.Select(p => new Restaurant
        {
            PlaceId = PlaceId.From(p.Id),
            Name = p.DisplayName?.Text ?? string.Empty,
            Address = p.FormattedAddress ?? string.Empty,
            Latitude = p.Location?.Latitude ?? 0,
            Longitude = p.Location?.Longitude ?? 0,
            AverageRating = p.Rating ?? 0,
            TotalReviews = p.UserRatingCount ?? 0,
            Region = DetermineRegion(latitude, longitude),
            CachedAt = DateTimeOffset.UtcNow
        }).ToList() ?? new List<Restaurant>();
    }

    /// <summary>
    /// Determines region code from coordinates (simplified implementation)
    /// Returns ISO 3166-1 alpha-2 country codes for leaderboard compatibility
    /// </summary>
    private static RegionCode DetermineRegion(double latitude, double longitude)
    {
        // Simplified region determination based on latitude/longitude
        // United States
        if (latitude >= 24.0 && latitude <= 50.0 && longitude >= -125.0 && longitude <= -66.0)
        {
            return RegionCode.From(CountryRegion.US);
        }

        // Canada
        if (latitude >= 41.0 && latitude <= 84.0 && longitude >= -141.0 && longitude <= -52.0)
        {
            return RegionCode.From(CountryRegion.CA);
        }

        // United Kingdom (rough bounds)
        if (latitude >= 49.0 && latitude <= 61.0 && longitude >= -8.0 && longitude <= 2.0)
        {
            return RegionCode.From(CountryRegion.GB);
        }

        // Australia
        if (latitude >= -44.0 && latitude <= -10.0 && longitude >= 112.0 && longitude <= 154.0)
        {
            return RegionCode.From(CountryRegion.AU);
        }

        // Default to US if unknown
        return RegionCode.Default;
    }

    /// <summary>
    /// Up to five of the newest reviews, from the legacy Place Details API. The Places API (New)
    /// returns only the five "most relevant" and cannot sort, so this is the one way past five.
    /// Empty when the legacy API refuses (not enabled on the key, quota); throws on transport errors.
    /// </summary>
    public async Task<List<Review>> GetNewestReviewsAsync(PlaceId placeId, CancellationToken cancellationToken = default)
    {
        var url = $"https://maps.googleapis.com/maps/api/place/details/json?place_id={Uri.EscapeDataString(placeId.Value)}&fields=reviews&reviews_sort=newest&key={_apiKey}";
        using var doc = JsonDocument.Parse(await _httpClient.GetStringAsync(url, cancellationToken));
        var root = doc.RootElement;
        if (root.GetProperty("status").GetString() != "OK")
        {
            _logger.LogWarning("Legacy Place Details refused newest reviews for {PlaceId}: {Status}",
                placeId, root.GetProperty("status").GetString());
            return [];
        }

        return root.TryGetProperty("result", out var result) && result.TryGetProperty("reviews", out var reviews)
            ? [.. reviews.EnumerateArray().Select(r => new Review
            {
                AuthorName = r.TryGetProperty("author_name", out var a) ? a.GetString() ?? "Anonymous" : "Anonymous",
                Rating = r.TryGetProperty("rating", out var stars) ? stars.GetInt32() : 0,
                Text = r.TryGetProperty("text", out var t) ? t.GetString() ?? string.Empty : string.Empty,
                Time = r.TryGetProperty("time", out var at) ? DateTimeOffset.FromUnixTimeSeconds(at.GetInt64()) : DateTimeOffset.UtcNow
            })]
            : [];
    }

    /// <summary>
    /// Gets detailed place information including reviews
    /// </summary>
    public async Task<Restaurant?> GetPlaceDetailsAsync(PlaceId placeId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Fetching place details for {PlaceId}", placeId);

        var request = new HttpRequestMessage(HttpMethod.Get, $"https://places.googleapis.com/v1/places/{Uri.EscapeDataString(placeId.Value)}");
        request.Headers.Add("X-Goog-Api-Key", _apiKey);
        request.Headers.Add("X-Goog-FieldMask", "id,displayName,formattedAddress,location,rating,userRatingCount,reviews");

        var response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Google Places API error fetching details for {PlaceId}: {StatusCode} - {ErrorContent}",
                placeId, response.StatusCode, errorContent);

            if (response.StatusCode is System.Net.HttpStatusCode.NotFound
                                    or System.Net.HttpStatusCode.BadRequest)
            {
                // Invalid or expired place ID — treat as not found
                return null;
            }

            // Include the actual Google API error body so callers can surface it for debugging
            throw new HttpRequestException(
                $"Google Places API error {(int)response.StatusCode}: {errorContent}",
                inner: null,
                statusCode: response.StatusCode);
        }

        var result = await response.Content.ReadFromJsonAsync<PlaceDetailsResponse>(cancellationToken: cancellationToken);

        if (result == null)
        {
            return null;
        }

        // The API returns place details directly (not nested in a "place" property)
        var place = result;
        var restaurant = new Restaurant
        {
            PlaceId = PlaceId.From(place.Id ?? placeId.Value),
            Name = place.DisplayName?.Text ?? string.Empty,
            Address = place.FormattedAddress ?? string.Empty,
            Latitude = place.Location?.Latitude ?? 0,
            Longitude = place.Location?.Longitude ?? 0,
            AverageRating = place.Rating ?? 0,
            TotalReviews = place.UserRatingCount ?? 0,
            Region = DetermineRegion(place.Location?.Latitude ?? 0, place.Location?.Longitude ?? 0),
            CachedAt = DateTimeOffset.UtcNow,
            Reviews = place.Reviews?.Select(r => new Review
            {
                AuthorName = r.AuthorAttribution?.DisplayName ?? "Anonymous",
                Rating = r.Rating ?? 0,
                Text = r.Text?.Text ?? r.OriginalText?.Text ?? string.Empty,
                Time = !string.IsNullOrEmpty(r.PublishTime) && DateTimeOffset.TryParse(r.PublishTime, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var publishTime)
                    ? publishTime
                    : DateTimeOffset.UtcNow,
                StrangenessScore = 0 // Will be calculated later by AI
            }).ToList() ?? new List<Review>()
        };

        _logger.LogInformation("Fetched {ReviewCount} reviews for place {PlaceId}", restaurant.Reviews.Count, placeId);

        return restaurant;
    }
}
