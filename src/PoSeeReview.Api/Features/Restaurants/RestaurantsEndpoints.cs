using PoSeeReview.Api.Telemetry;
using PoSeeReview.Shared.Dtos;
using PoSeeReview.Shared.Contracts;
using PoSeeReview.Shared.Ids;
using PoSeeReview.Shared.Enums;

namespace PoSeeReview.Api.Features.Restaurants;

/// <summary>
/// Restaurant discovery slice. Maps <c>/api/restaurants</c> (NET_RULES 3.3).
/// </summary>
internal static class RestaurantsEndpoints
{
    public static IEndpointRouteBuilder MapRestaurantsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/restaurants").WithTags("Restaurants");

        group.MapGet("/nearby", GetNearbyRestaurants);
        group.MapGet("/search", SearchRestaurantsByLocation);

        return app;
    }

    private static bool IsDevPlaceholderKey(IConfiguration config) =>
        (config["GoogleMaps:ApiKey"] ?? string.Empty).StartsWith("dev-placeholder", StringComparison.OrdinalIgnoreCase);

    private static async Task<IResult> GetNearbyRestaurants(
        double? latitude,
        double? longitude,
        IRestaurantService restaurantService,
        ILogger<IRestaurantService> logger,
        IWebHostEnvironment env,
        IConfiguration config,
        HttpContext http,
        int limit = 10,
        string? category = null)
    {
        if (!latitude.HasValue || !longitude.HasValue)
        {
            return Results.Problem(statusCode: 400, title: "Bad Request",
                detail: "Both latitude and longitude are required");
        }

        if (!PlaceCategories.TryParse(category, out var kind))
        {
            return Results.Problem(statusCode: 400, title: "Bad Request",
                detail: "Category must be 'restaurant' or 'apartment'");
        }

        try
        {
            var lat = latitude.Value;
            var lon = longitude.Value;

            logger.LogInformation("Getting nearby restaurants at ({Latitude}, {Longitude}), limit {Limit}", lat, lon, limit);

            if (IsDevPlaceholderKey(config))
            {
                logger.LogWarning(
                    "Skipping Google Maps call: GoogleMaps:ApiKey is a dev placeholder. " +
                    "Add a real key to appsettings.Development.json or Key Vault to enable nearby search.");

                var devResponse = new NearbyRestaurantsResponse
                {
                    Restaurants = new List<RestaurantDto>(),
                    TotalCount = 0,
                    CachedAt = DateTimeOffset.UtcNow,
                    Stale = true,
                    // Populated so the client can explain the real cause instead of falling
                    // back to a generic "no results here" that sends the user hunting for a
                    // different city when the actual problem is an unconfigured API key.
                    StaleReason = "Restaurant search is not configured: GoogleMaps:ApiKey is still a "
                        + "dev placeholder. Add a real key to appsettings.Development.json or Key Vault."
                };
                return Results.Ok(devResponse);
            }

            var restaurants = await restaurantService.GetNearbyRestaurantsAsync(lat, lon, limit, kind, http.RequestAborted);

            var restaurantDtos = restaurants.Select(r => new RestaurantDto
            {
                PlaceId = r.PlaceId.Value,
                Name = r.Name,
                Address = r.Address,
                Latitude = r.Latitude,
                Longitude = r.Longitude,
                AverageRating = r.AverageRating,
                TotalReviews = r.TotalReviews,
                Region = r.Region.Value,
                Distance = GeoUtils.CalculateDistance(lat, lon, r.Latitude, r.Longitude),
                Category = r.Category.ToWire()
            }).ToList();

            var response = new NearbyRestaurantsResponse
            {
                Restaurants = restaurantDtos,
                TotalCount = restaurantDtos.Count,
                CachedAt = DateTimeOffset.UtcNow
            };

            return Results.Ok(response);
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "Invalid request parameters");
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid Parameters", detail: ex.Message);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Nearby restaurant search cancelled (client disconnected or timed out)");
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Request Cancelled", detail: "The request was cancelled before it could complete.");
        }
        catch (HttpRequestException ex)
        {
            var upstream = (int?)ex.StatusCode;
            var isClientError = upstream is >= 400 and < 500;
            var statusCode = isClientError ? StatusCodes.Status400BadRequest : StatusCodes.Status503ServiceUnavailable;
            var title = isClientError ? "Google Maps Request Rejected" : "Google Maps API Unavailable";

            PoSeeReviewTelemetry.ComicGenerationErrors.Add(1, new[]
            {
                new KeyValuePair<string, object?>("error_type", isClientError ? "google_maps_4xx" : "google_maps_5xx"),
                new KeyValuePair<string, object?>("provider", "google-maps"),
                new KeyValuePair<string, object?>("phase", "nearby_search"),
                new KeyValuePair<string, object?>("upstream_status", upstream ?? 0)
            });

            logger.LogError(ex, "Google Maps API error for nearby search. UpstreamStatus={UpstreamStatus} ClientError={ClientError}",
                upstream, isClientError);

            var detail = env.IsDevelopment()
                ? ex.Message
                : (isClientError
                    ? "The nearby search request was rejected by the upstream provider."
                    : "Unable to fetch restaurant data from Google Maps API");

            return Results.Problem(statusCode: statusCode, title: title, detail: detail, instance: http.Request.Path);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error in nearby restaurant search at ({Latitude}, {Longitude})", latitude, longitude);
            throw;
        }
    }

    private static async Task<IResult> SearchRestaurantsByLocation(
        // Nullable so an absent ?location= reaches the 400 guard below. Bound as non-nullable,
        // minimal APIs threw BadHttpRequestException first and the caller saw a 500.
        string? location,
        IRestaurantService restaurantService,
        ILogger<IRestaurantService> logger,
        IWebHostEnvironment env,
        IConfiguration config,
        HttpContext http,
        int limit = 10,
        string? category = null)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return Results.Problem(statusCode: 400, title: "Bad Request", detail: "Location query is required");
        }

        (double lat, double lon)? coordinates;
        try
        {
            coordinates = await restaurantService.GeocodeLocationAsync(location, http.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Geocoding cancelled for location '{Location}' (client disconnected)", location);
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Request Cancelled", detail: "The request was cancelled before geocoding could complete.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Geocoding failed for location '{Location}': {Message}", location, ex.Message);
            var detail = env.IsDevelopment() ? ex.Message : "Unable to resolve the specified location.";
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Geocoding Failed", detail: detail);
        }

        if (coordinates == null)
        {
            logger.LogWarning("Location '{Location}' not recognized — returning 400", location);
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Unrecognized Location",
                detail: $"Location '{location}' could not be geocoded. Try including city/state or a full ZIP/postal code.");
        }

        return await GetNearbyRestaurants(coordinates.Value.lat, coordinates.Value.lon,
            restaurantService, logger, env, config, http, limit, category);
    }
}
