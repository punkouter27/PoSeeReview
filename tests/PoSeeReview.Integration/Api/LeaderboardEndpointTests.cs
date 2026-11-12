using System.Net.Http.Json;
using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PoSeeReview.Api.Features.Comics;
using PoSeeReview.Api.Features.Leaderboard;
using PoSeeReview.Api.Features.Moderation;
using PoSeeReview.Api.Features.Restaurants;
using PoSeeReview.Api;
using PoSeeReview.Shared.Dtos;
using Xunit.Abstractions;
using Xunit;
using PoSeeReview.Shared.Contracts;
using PoSeeReview.Shared.Ids;
using PoSeeReview.Shared.Enums;

namespace PoSeeReview.Integration.Api;

/// <summary>
/// Integration tests for GET /api/leaderboard endpoint
/// Tests regional leaderboard retrieval with real Azure Table Storage (Azurite)
/// </summary>
[Trait("Tier", "Integration")]
[Trait("Domain", "Api")]
public class LeaderboardEndpointTests : IClassFixture<CustomWebApplicationFactory<Program>>
{
    private readonly HttpClient _client;
    private readonly ITestOutputHelper _output;
    private readonly CustomWebApplicationFactory<Program> _factory;

    public LeaderboardEndpointTests(
        CustomWebApplicationFactory<Program> factory,
        ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
    }

    [Fact]

    [Trait("Category", "Integration")]
    public async Task GetLeaderboard_WithValidRegion_ReturnsTop10Entries()
    {
        // Arrange
        var region = "US-WA-TEST";
        await SeedLeaderboardEntries(region, 15);

        // Act
        var response = await _client.GetAsync($"/api/leaderboard?region={region}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadFromJsonAsync<LeaderboardResponse>();
        Assert.NotNull(content);
        Assert.NotNull(content.Entries);
        Assert.Equal(10, content.Entries.Count); // Default limit is 10
        Assert.Equal(region, content.Region);
        Assert.True(content.LastUpdated > DateTimeOffset.MinValue);

        // Verify descending order by score
        for (int i = 0; i < content.Entries.Count - 1; i++)
        {
            Assert.True(content.Entries[i].StrangenessScore >= content.Entries[i + 1].StrangenessScore,
                $"Entry {i} score should be >= entry {i + 1} score");
        }

        // Verify ranks are sequential
        for (int i = 0; i < content.Entries.Count; i++)
        {
            Assert.Equal(i + 1, content.Entries[i].Rank);
        }

        _output.WriteLine($"Retrieved {content.Entries.Count} leaderboard entries for {region}");
        _output.WriteLine($"Top score: {content.Entries[0].StrangenessScore:F2}");
        _output.WriteLine($"Top restaurant: {content.Entries[0].RestaurantName}");
    }

    [Fact]

    [Trait("Category", "Integration")]
    public async Task GetLeaderboard_WithCustomLimit_ReturnsRequestedCount()
    {
        // Arrange
        var region = "US-CA-TEST";
        var limit = 5;
        await SeedLeaderboardEntries(region, 10);

        // Act
        var response = await _client.GetAsync($"/api/leaderboard?region={region}&limit={limit}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadFromJsonAsync<LeaderboardResponse>();
        Assert.NotNull(content);
        Assert.Equal(limit, content.Entries.Count);
    }

    [Fact]

    [Trait("Category", "Integration")]
    public async Task GetLeaderboard_WithFewerEntriesThanLimit_ReturnsAvailableEntries()
    {
        // Arrange
        var region = "US-MT-TEST";
        await SeedLeaderboardEntries(region, 3);

        // Act
        var response = await _client.GetAsync($"/api/leaderboard?region={region}&limit=10");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadFromJsonAsync<LeaderboardResponse>();
        Assert.NotNull(content);
        Assert.Equal(3, content.Entries.Count);
    }

    [Fact]

    [Trait("Category", "Integration")]
    public async Task GetLeaderboard_WithEmptyRegion_ReturnsWorldwideBoard()
    {
        // Act - an empty region is the worldwide board, not a silent default to US
        var response = await _client.GetAsync("/api/leaderboard?region=");

        // Assert - Should succeed with default region
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadFromJsonAsync<LeaderboardResponse>();
        Assert.NotNull(content);
        Assert.Equal("ALL", content.Region);
    }

    [Fact]

    [Trait("Category", "Integration")]
    public async Task GetLeaderboard_WithInvalidRegionFormat_ReturnsBadRequest()
    {
        // Act - Invalid format (should be XX-XX-XXX)
        var response = await _client.GetAsync("/api/leaderboard?region=INVALID");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]

    [Trait("Category", "Integration")]
    public async Task GetLeaderboard_WithExcessiveLimit_ReturnsBadRequest()
    {
        // Act
        var response = await _client.GetAsync("/api/leaderboard?region=US-WA-TEST&limit=100");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GET_Leaderboard_Filters_By_Region_Only()
    {
        // Arrange - Create entries for multiple regions
        await SeedLeaderboardEntries("US", 5);
        await SeedLeaderboardEntries("CA", 5);

        // Act
        var response = await _client.GetAsync("/api/leaderboard?region=CA&limit=10");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<LeaderboardResponse>();
        Assert.NotNull(result);
        Assert.Equal("CA", result.Region);

        // Verify all entries are from CA region only
        Assert.All(result.Entries, entry => Assert.Equal("CA", entry.Region));
    }

    [Fact]
    public async Task GetLeaderboard_OmitsAPlaceModerationIsWithholding()
    {
        // Hide was only enforced under /api/comics: the board kept showing a withheld place's
        // name, score and artwork. A region of its own, so the 60-second output cache is cold.
        var region = "US-OR-TEST";
        await SeedLeaderboardEntries(region, 3);

        using var scope = _factory.Services.CreateScope();
        var moderation = scope.ServiceProvider.GetRequiredService<ModerationRepository>();
        var hiddenPlace = PlaceId.From("ChIJTestSeed001");
        await moderation.SetStateAsync(hiddenPlace, hidden: true, suppressed: false, "test", "test");

        try
        {
            var content = await _client.GetFromJsonAsync<LeaderboardResponse>($"/api/leaderboard?region={region}");

            Assert.NotNull(content);
            Assert.Equal(["ChIJTestSeed000", "ChIJTestSeed002"], content.Entries.Select(e => e.PlaceId));
            Assert.Equal([1, 2], content.Entries.Select(e => e.Rank));
        }
        finally
        {
            // The seed ids are shared by every region in this class.
            await moderation.SetStateAsync(hiddenPlace, hidden: false, suppressed: false, "test", "test");
        }
    }

    private async Task SeedLeaderboardEntries(string region, int count)
    {
        using var scope = _factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ILeaderboardRepository>();
        var comics = scope.ServiceProvider.GetRequiredService<IComicRepository>();
        var promptVersion = scope.ServiceProvider.GetRequiredService<IOptions<ComicOptions>>().Value.PromptVersion;

        for (int i = 0; i < count; i++)
        {
            var entry = new LeaderboardEntry
            {
                PlaceId = PlaceId.From($"ChIJTestSeed{i:D3}"),
                RestaurantName = $"Test Restaurant {i + 1}",
                Address = $"{i + 1} Test Street, Test City",
                Region = RegionCode.From(region),
                StrangenessScore = 100 - (i * 2), // Descending scores
                ComicBlobUrl = $"https://test.blob.core.windows.net/comic{i}.png",
                LastUpdated = DateTimeOffset.UtcNow
            };

            await repository.UpsertAsync(entry);

            // The board only lists places whose comic would open, so each row needs a live one.
            await comics.UpsertAsync(new Comic
            {
                Id = ComicId.New(),
                PlaceId = entry.PlaceId,
                RestaurantName = entry.RestaurantName,
                ImageUrl = entry.ComicBlobUrl,
                StrangenessScore = (int)entry.StrangenessScore,
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
                PromptVersion = promptVersion
            });
        }

        _output.WriteLine($"Seeded {count} entries for region {region}");
    }

    // Response DTO for deserialization
    private sealed class LeaderboardResponse
    {
        public List<LeaderboardEntryDto> Entries { get; set; } = new();
        public string Region { get; set; } = string.Empty;
        public DateTimeOffset LastUpdated { get; set; } = DateTimeOffset.MinValue;
    }
}
