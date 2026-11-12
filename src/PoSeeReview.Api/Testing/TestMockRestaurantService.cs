using PoSeeReview.Shared.Contracts;
using PoSeeReview.Shared.Ids;

namespace PoSeeReview.Api.Testing;

/// <summary>
/// Deterministic in-memory restaurant catalog for local Test/E2EUI verification runs when
/// <c>UseMockData</c> is enabled in the <c>Test</c> environment, avoiding live Google Maps calls.
/// </summary>
internal sealed class TestMockRestaurantService : IRestaurantService, IMockable
{
    private static readonly IReadOnlyList<Restaurant> Catalog =
    [
        new Restaurant
        {
            PlaceId = PlaceId.From("ChIJMockPikeChowder001"),
            Name = "Pike Place Weird Chowder",
            Address = "1530 Post Alley, Seattle, WA 98101",
            Latitude = 47.6088,
            Longitude = -122.3405,
            Region = RegionCode.From("US-WA-Seattle"),
            AverageRating = 4.6,
            TotalReviews = 312,
            CachedAt = DateTimeOffset.UtcNow,
            Reviews =
            [
                new Review
                {
                    AuthorName = "Terry",
                    Rating = 5,
                    StrangenessScore = 78,
                    Time = DateTimeOffset.UtcNow.AddDays(-3),
                    Text = "A seagull wearing a tiny bowtie judged our sourdough bowl from the windowsill while the chef juggled ladles."
                },
                new Review
                {
                    AuthorName = "Morgan",
                    Rating = 4,
                    StrangenessScore = 64,
                    Time = DateTimeOffset.UtcNow.AddDays(-9),
                    Text = "They rang a foghorn every time someone ordered clam nectar and handed out paper pirate hats."
                }
            ]
        },
        new Restaurant
        {
            PlaceId = PlaceId.From("ChIJMockPickleBistro002"),
            Name = "Space Needle Pickle Bistro",
            Address = "400 Broad St, Seattle, WA 98109",
            Latitude = 47.6205,
            Longitude = -122.3493,
            Region = RegionCode.From("US-WA-Seattle"),
            AverageRating = 4.4,
            TotalReviews = 185,
            CachedAt = DateTimeOffset.UtcNow,
            Reviews =
            [
                new Review
                {
                    AuthorName = "Jamie",
                    Rating = 5,
                    StrangenessScore = 71,
                    Time = DateTimeOffset.UtcNow.AddDays(-5),
                    Text = "Our waiter spoke exclusively in pickle puns and insisted we salute the giant dill sculpture before dessert."
                }
            ]
        },
        new Restaurant
        {
            PlaceId = PlaceId.From("ChIJMockGoatEspresso003"),
            Name = "Ballard Goat Espresso",
            Address = "5410 Ballard Ave NW, Seattle, WA 98107",
            Latitude = 47.6669,
            Longitude = -122.3847,
            Region = RegionCode.From("US-WA-Seattle"),
            AverageRating = 4.8,
            TotalReviews = 94,
            CachedAt = DateTimeOffset.UtcNow,
            Reviews =
            [
                new Review
                {
                    AuthorName = "Alex",
                    Rating = 5,
                    StrangenessScore = 82,
                    Time = DateTimeOffset.UtcNow.AddDays(-2),
                    Text = "Two miniature goats graded everyone's latte foam art with tiny wooden paddles on the patio."
                }
            ]
        }
    ];

    public Task<List<Restaurant>> GetNearbyRestaurantsAsync(
        double latitude,
        double longitude,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            return Task.FromResult(new List<Restaurant>());
        }

        return Task.FromResult(Catalog.Take(limit).ToList());
    }

    public Task<Restaurant> GetRestaurantByPlaceIdAsync(
        PlaceId placeId,
        CancellationToken cancellationToken = default)
    {
        var match = Catalog.FirstOrDefault(r =>
            string.Equals(r.PlaceId.Value, placeId.Value, StringComparison.Ordinal));

        return match is not null
            ? Task.FromResult(match)
            : throw new KeyNotFoundException($"Restaurant '{placeId}' not found in test catalog.");
    }

    public Task<(double lat, double lon)?> GeocodeLocationAsync(
        string locationQuery,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(locationQuery))
        {
            return Task.FromResult<(double lat, double lon)?>(null);
        }

        return Task.FromResult<(double lat, double lon)?>((47.6062, -122.3321));
    }
}
