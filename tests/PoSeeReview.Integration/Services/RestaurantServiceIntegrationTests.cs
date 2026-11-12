using Azure.Data.Tables;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PoSeeReview.Api.Storage;
using Microsoft.Extensions.Logging;
using PoSeeReview.Api.Features.Comics;
using PoSeeReview.Api.Features.Restaurants;
using Xunit.Abstractions;
using Xunit;
using PoSeeReview.Shared.Contracts;
using PoSeeReview.Shared.Ids;
using PoSeeReview.Shared.Enums;

namespace PoSeeReview.Integration.Services;

/// <summary>
/// Integration tests for RestaurantService that verify review fetching from Google Maps API
/// 
/// NOTE: These tests require a real Google Maps API key and will make actual API calls.
/// Use [Trait("Category", "Expensive")] to filter them out in normal test runs.
/// </summary>
[Trait("Tier", "Integration")]
[Trait("Domain", "Services")]
[Trait("Suite", "CriticalPath")]
public class RestaurantServiceIntegrationTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private IConfiguration _configuration = null!;
    private GoogleMapsService _googleMapsService = null!;
    private RestaurantService _restaurantService = null!;

    // La'Caj Seafood place ID (Camp Springs, MD)
    private const string LaCajSeafoodPlaceId = "ChIJB0Oz_rC9t4kRrRgfCQ27RKQ";

    public RestaurantServiceIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private bool HasValidApiKey =>
        _configuration != null &&
        !string.IsNullOrEmpty(_configuration["GoogleMaps:ApiKey"]) &&
        _configuration["GoogleMaps:ApiKey"]!.Length >= 30 &&
        !_configuration["GoogleMaps:ApiKey"]!.Contains("your-");

    public async Task InitializeAsync()
    {
        // Load configuration from appsettings.Development.json
        var basePath = Path.GetFullPath(Path.Combine(
            Directory.GetCurrentDirectory(),
            "..", "..", "..", "..", "..", "src", "PoSeeReview.Api"));

        _configuration = new ConfigurationBuilder()
            .SetBasePath(basePath)
            // Stub key prevents GoogleMapsService ctor from throwing. HasValidApiKey
            // checks Length >= 30, so tests skip unless a real key comes in via env vars.
            // appsettings.Development.json is intentionally excluded here: per PoTest
            // rule #7, real external-API credentials must be supplied by CI via env vars,
            // not auto-loaded from local developer config.
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GoogleMaps:ApiKey"] = "test-stub-key-placeholder",
            })
            .AddEnvironmentVariables()
            .Build();

        // Guard: if no valid API key, skip expensive service/storage setup entirely.
        // Each test body also has "if (!HasValidApiKey) return;" so tests are counted as passed.
        if (!HasValidApiKey)
            return;

        // Setup services
        var httpClient = new HttpClient();
        var googleMapsLogger = NullLogger<GoogleMapsService>.Instance;
        _googleMapsService = new GoogleMapsService(httpClient, googleMapsLogger, _configuration);

        // Setup Table Storage client (uses local Azurite)
        var connectionString = _configuration["AzureStorage:ConnectionString"] ?? "UseDevelopmentStorage=true";
        var tableServiceClient = new TableServiceClient(connectionString);

        // Setup repository with table storage
        var restaurantRepository = new RestaurantRepository(tableServiceClient, Options.Create(new AzureStorageOptions()), NullLogger<RestaurantRepository>.Instance);

        var restaurantLogger = NullLogger<RestaurantService>.Instance;
        _restaurantService = new RestaurantService(
            restaurantRepository,
            _googleMapsService,
            restaurantLogger);

        await Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Category", "RequiresGoogleMapsAPI")]
    public async Task GetRestaurantByPlaceId_LaCajSeafood_ShouldReturnRestaurantWithReviews()
    {
        // Skip test if configuration is missing
        if (!HasValidApiKey)
        {
            _output.WriteLine("⚠️ Skipping test: Google Maps API key not found or invalid");
            _output.WriteLine("💡 Configure the API key in user secrets:");
            _output.WriteLine("   dotnet user-secrets set \"GoogleMaps:ApiKey\" \"YOUR_API_KEY\" --project src/PoSeeReview.Api");
            return;
        }

        _output.WriteLine($"🔍 Fetching restaurant details for La'Caj Seafood (Place ID: {LaCajSeafoodPlaceId})");
        _output.WriteLine("");

        // Act
        var restaurant = await _restaurantService.GetRestaurantByPlaceIdAsync(PlaceId.From(LaCajSeafoodPlaceId));

        // Assert - Restaurant exists
        Assert.NotNull(restaurant);
        Assert.Equal(PlaceId.From(LaCajSeafoodPlaceId), restaurant.PlaceId);
        Assert.NotEmpty(restaurant.Name);
        Assert.NotEmpty(restaurant.Address);

        _output.WriteLine($"✅ Restaurant Found:");
        _output.WriteLine($"   Name: {restaurant.Name}");
        _output.WriteLine($"   Address: {restaurant.Address}");
        _output.WriteLine($"   Rating: {restaurant.AverageRating:F1} ⭐");
        _output.WriteLine($"   Total Reviews: {restaurant.TotalReviews}");
        _output.WriteLine($"   Latitude: {restaurant.Latitude}");
        _output.WriteLine($"   Longitude: {restaurant.Longitude}");
        _output.WriteLine("");

        // Assert - Reviews exist
        Assert.NotNull(restaurant.Reviews);
        Assert.NotEmpty(restaurant.Reviews);

        // La'Caj Seafood should have reviews (it has 230+ reviews according to Google Maps)
        Assert.True(restaurant.Reviews.Count > 0,
            $"Expected at least 1 review, but got {restaurant.Reviews.Count}");

        _output.WriteLine($"📝 Reviews Fetched: {restaurant.Reviews.Count}");
        _output.WriteLine("");

        // Display first few reviews
        var reviewsToShow = Math.Min(3, restaurant.Reviews.Count);
        _output.WriteLine($"Sample Reviews (showing {reviewsToShow} of {restaurant.Reviews.Count}):");
        for (int i = 0; i < reviewsToShow; i++)
        {
            var review = restaurant.Reviews[i];
            _output.WriteLine($"   {i + 1}. {review.AuthorName} - {review.Rating} ⭐");
            _output.WriteLine($"      \"{TruncateText(review.Text, 100)}\"");
            _output.WriteLine($"      Published: {review.Time:yyyy-MM-dd}");
            _output.WriteLine("");
        }

        // Assert - Review quality
        var firstReview = restaurant.Reviews[0];
        Assert.NotEmpty(firstReview.AuthorName);
        Assert.InRange(firstReview.Rating, 1, 5);
        Assert.NotEmpty(firstReview.Text);
        Assert.True(firstReview.Time != default, "Review time should be set");

        _output.WriteLine("✅ All assertions passed!");
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Category", "RequiresGoogleMapsAPI")]
    public async Task GetPlaceDetailsAsync_LaCajSeafood_ShouldReturnAtLeast5Reviews()
    {
        // Skip test if configuration is missing
        if (!HasValidApiKey)
        {
            _output.WriteLine("⚠️ Skipping test: Google Maps API key not found or invalid");
            return;
        }

        _output.WriteLine($"🔍 Fetching place details directly from Google Maps API");
        _output.WriteLine("");

        // Act - Call the Google Maps service directly
        var restaurant = await _googleMapsService.GetPlaceDetailsAsync(PlaceId.From(LaCajSeafoodPlaceId));

        // Assert
        Assert.NotNull(restaurant);
        Assert.Equal(PlaceId.From(LaCajSeafoodPlaceId), restaurant.PlaceId);
        Assert.NotNull(restaurant.Reviews);

        _output.WriteLine($"✅ Place Details Retrieved:");
        _output.WriteLine($"   Name: {restaurant.Name}");
        _output.WriteLine($"   Reviews Count: {restaurant.Reviews.Count}");
        _output.WriteLine("");

        // La'Caj Seafood should have at least 5 reviews to generate comics
        Assert.True(restaurant.Reviews.Count >= 5,
            $"Expected at least 5 reviews for comic generation, but got {restaurant.Reviews.Count}. " +
            $"This restaurant has 230+ reviews on Google Maps.");

        // Verify reviews have content
        foreach (var review in restaurant.Reviews.Take(5))
        {
            Assert.NotEmpty(review.Text);
            Assert.InRange(review.Rating, 1, 5);
            _output.WriteLine($"   ✓ Review by {review.AuthorName}: {review.Rating}⭐ - {review.Text.Length} chars");
        }

        _output.WriteLine("");
        _output.WriteLine($"✅ Successfully retrieved {restaurant.Reviews.Count} reviews - sufficient for comic generation!");
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Category", "RequiresGoogleMapsAPI")]
    public async Task GetRestaurantByPlaceId_ShouldCacheReviews()
    {
        // Skip test if configuration is missing
        if (!HasValidApiKey)
        {
            _output.WriteLine("⚠️ Skipping test: Google Maps API key not found or invalid");
            return;
        }

        _output.WriteLine($"🔍 Testing review caching for La'Caj Seafood");
        _output.WriteLine("");

        // Act - First call should fetch from API
        _output.WriteLine("📡 First call (should fetch from Google Maps API)...");
        var restaurant1 = await _restaurantService.GetRestaurantByPlaceIdAsync(PlaceId.From(LaCajSeafoodPlaceId));
        var reviewCount1 = restaurant1.Reviews?.Count ?? 0;
        _output.WriteLine($"   Retrieved {reviewCount1} reviews");
        _output.WriteLine("");

        // Act - Second call should use cache
        _output.WriteLine("💾 Second call (should use cache)...");
        var restaurant2 = await _restaurantService.GetRestaurantByPlaceIdAsync(PlaceId.From(LaCajSeafoodPlaceId));
        var reviewCount2 = restaurant2.Reviews?.Count ?? 0;
        _output.WriteLine($"   Retrieved {reviewCount2} reviews");
        _output.WriteLine("");

        // Assert - Both calls should return the same data
        Assert.Equal(reviewCount1, reviewCount2);
        Assert.True(reviewCount1 > 0, "Should have retrieved reviews");
        Assert.Equal(restaurant1.PlaceId, restaurant2.PlaceId);
        Assert.Equal(restaurant1.Name, restaurant2.Name);

        _output.WriteLine($"✅ Cache working correctly - both calls returned {reviewCount1} reviews");
    }

    private static string TruncateText(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text;

        return string.Concat(text.AsSpan(0, maxLength), "...");
    }
}
