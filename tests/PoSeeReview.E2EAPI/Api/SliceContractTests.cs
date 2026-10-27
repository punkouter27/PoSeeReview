using System.Net;
using System.Net.Http.Json;
using PoSeeReview.Api;
using PoSeeReview.Shared.Dtos;
using Xunit;

namespace PoSeeReview.E2EAPI.Api;

/// <summary>
/// Request/response contract for the Leaderboard, Restaurants and Takedowns slices.
/// Authenticated via the Test-only FakeAuth scheme (NET_RULES 4.4) so the real handlers run
/// without an interactive login.
/// </summary>
public class SliceContractTests(CustomWebApplicationFactory<Program> factory)
    : IClassFixture<CustomWebApplicationFactory<Program>>
{
    private HttpClient CreateAuthenticatedClient()
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Fake-User", "slice-contract-user");
        return client;
    }

    // ── Leaderboard ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetLeaderboard_Default_Returns200WithRegion()
    {
        var response = await CreateAuthenticatedClient().GetAsync("/api/leaderboard");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<LeaderboardResponse>();
        Assert.NotNull(payload);
        Assert.Equal("ALL", payload.Region); // no region is the worldwide board
    }

    [Theory]
    [InlineData("not-a-region")]
    [InlineData("U")]
    [InlineData("123")]
    public async Task GetLeaderboard_InvalidRegion_Returns400(string region)
    {
        var response = await CreateAuthenticatedClient().GetAsync($"/api/leaderboard?region={region}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Restaurants ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetNearby_MissingCoordinates_Returns400()
    {
        var response = await CreateAuthenticatedClient().GetAsync("/api/restaurants/nearby");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetNearby_ReturnsWellFormedEnvelope()
    {
        var payload = await CreateAuthenticatedClient()
            .GetFromJsonAsync<NearbyRestaurantsResponse>("/api/restaurants/nearby?latitude=47.6062&longitude=-122.3321");

        Assert.NotNull(payload);
        Assert.NotNull(payload.Restaurants);
        Assert.Equal(payload.Restaurants.Count, payload.TotalCount);
    }

    // ── Comics: place ids and retired routes ────────────────────────────────

    [Theory]
    [InlineData("/share/place-1/card.png", HttpStatusCode.NotFound)] // well-formed, no comic
    [InlineData("/share/bad%20id/card.png", HttpStatusCode.BadRequest)]
    [InlineData("/api/comics/bad%20id", HttpStatusCode.BadRequest)]
    [InlineData("/duel/place-1/place-2", HttpStatusCode.Redirect)] // retired page, old links land on the board
    public async Task PlaceIdRoutes_RejectMalformedIds_AndRetiredDuelRedirects(string path, HttpStatusCode expected)
    {
        var response = await CreateAuthenticatedClient().GetAsync(path);

        Assert.Equal(expected, response.StatusCode);
    }

    // ── Takedowns ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("definitely-not-the-key")]
    public async Task PostTakedown_WithoutTheRightApiKey_IsNeverAccepted(string? apiKey)
    {
        var client = factory.CreateClient();
        if (apiKey is not null)
        {
            client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        }

        var response = await client.PostAsJsonAsync("/api/takedowns", new TakedownRequestDto
        {
            PlaceId = "ChIJTakedown123",
            Region = "US",
            ContactEmail = "owner@example.com",
            RequesterName = "Owner",
            Reason = "We do not consent to this content appearing on your platform."
        });

        // The key is deliberately absent from every appsettings file, so the filter fails
        // closed with 503; a configured-but-wrong key would yield 401. Never 2xx.
        Assert.True(
            response.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.Unauthorized,
            $"Expected 503 or 401, got {(int)response.StatusCode}");
    }
}
