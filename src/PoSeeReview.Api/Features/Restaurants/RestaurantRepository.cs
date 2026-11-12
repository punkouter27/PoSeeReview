using System.Text.Json;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PoSeeReview.Api.Storage;
using PoSeeReview.Shared.Contracts;
using PoSeeReview.Shared.Ids;
using PoSeeReview.Shared.Enums;

namespace PoSeeReview.Api.Features.Restaurants;

/// <summary>
/// Repository for restaurant data with 24-hour cache in Azure Table Storage
/// Table: PoSeeReviewRestaurants
/// Partition Key: RESTAURANT_{Region}, Row Key: {PlaceId}
/// </summary>
public class RestaurantRepository : TableStorageRepository<RestaurantEntity>
{
    private readonly ILogger<RestaurantRepository> _logger;
    private const int CacheExpirationHours = 24;

    public RestaurantRepository(
        TableServiceClient tableServiceClient,
        IOptions<AzureStorageOptions> options,
        ILogger<RestaurantRepository> logger)
        : base(tableServiceClient, options.Value.RestaurantsTableName, logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets restaurant by Google place ID
    /// Returns null if not cached or cache expired (> 24 hours)
    /// </summary>
    public async Task<Restaurant?> GetByPlaceIdAsync(PlaceId placeId, RegionCode region)
    {
        if (placeId.IsEmpty)
            throw new ArgumentException("PlaceId is required", nameof(placeId));

        var partitionKey = RestaurantEntity.CreatePartitionKey(region);
        var rowKey = RestaurantEntity.CreateRowKey(placeId);

        var entity = await GetByIdAsync(partitionKey, rowKey);

        if (entity == null)
            return null;

        // Check cache expiration
        if (!IsCacheValid(entity.CachedAt))
        {
            _logger.LogInformation("Cache expired for restaurant {PlaceId}", placeId);
            return null;
        }

        return FromEntity(entity);
    }

    /// <summary>
    /// Stores/updates restaurant with current timestamp
    /// </summary>
    public async Task UpsertRestaurantAsync(Restaurant restaurant)
    {
        ArgumentNullException.ThrowIfNull(restaurant);

        restaurant.CachedAt = DateTimeOffset.UtcNow;

        var entity = ToEntity(restaurant);
        await UpsertAsync(entity);

        _logger.LogInformation(
            "Cached restaurant {PlaceId} in region {Region}",
            restaurant.PlaceId,
            restaurant.Region);
    }

    /// <summary>
    /// Maps domain Restaurant to Azure Table Storage entity
    /// </summary>
    private static RestaurantEntity ToEntity(Restaurant restaurant)
    {
        return new RestaurantEntity
        {
            PartitionKey = RestaurantEntity.CreatePartitionKey(restaurant.Region),
            RowKey = RestaurantEntity.CreateRowKey(restaurant.PlaceId),
            PlaceId = restaurant.PlaceId.Value,
            Name = restaurant.Name,
            Address = restaurant.Address,
            Latitude = restaurant.Latitude,
            Longitude = restaurant.Longitude,
            Region = restaurant.Region.Value,
            AverageRating = restaurant.AverageRating,
            TotalReviews = restaurant.TotalReviews,
            ReviewsJson = JsonSerializer.Serialize(restaurant.Reviews),
            CachedAt = restaurant.CachedAt
        };
    }

    /// <summary>
    /// Maps Azure Table Storage entity to domain Restaurant
    /// </summary>
    private static Restaurant FromEntity(RestaurantEntity entity)
    {
        return new Restaurant
        {
            PlaceId = PlaceId.From(entity.PlaceId),
            Name = entity.Name,
            Address = entity.Address,
            Latitude = entity.Latitude,
            Longitude = entity.Longitude,
            Region = RegionCode.From(entity.Region),
            AverageRating = entity.AverageRating,
            TotalReviews = entity.TotalReviews,
            Reviews = string.IsNullOrWhiteSpace(entity.ReviewsJson)
                ? new List<Review>()
                : JsonSerializer.Deserialize<List<Review>>(entity.ReviewsJson) ?? new List<Review>(),
            CachedAt = entity.CachedAt
        };
    }

    /// <summary>
    /// Checks if cached data is still valid (within 24 hours)
    /// </summary>
    private static bool IsCacheValid(DateTimeOffset cachedAt)
    {
        return DateTimeOffset.UtcNow - cachedAt < TimeSpan.FromHours(CacheExpirationHours);
    }
}
