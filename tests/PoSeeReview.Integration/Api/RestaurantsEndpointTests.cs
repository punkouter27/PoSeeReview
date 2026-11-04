using System.Net.Http.Json;
using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using PoSeeReview.Api;
using Xunit.Abstractions;
using Xunit;

namespace PoSeeReview.Integration.Api;

/// <summary>
/// Integration tests for GET /api/restaurants endpoints
/// </summary>
[Trait("Tier", "Integration")]
[Trait("Domain", "Api")]
public class RestaurantsEndpointTests : IClassFixture<CustomWebApplicationFactory<Program>>
{
    private readonly HttpClient _client;
    private readonly ITestOutputHelper _output;

    public RestaurantsEndpointTests(CustomWebApplicationFactory<Program> factory, ITestOutputHelper output)
    {
        _client = factory.CreateClient();
        _output = output;
    }

    [Theory]
    [InlineData(91.0, -122.3321)]   // Invalid latitude > 90
    [InlineData(-91.0, -122.3321)]  // Invalid latitude < -90
    [InlineData(47.6062, 181.0)]    // Invalid longitude > 180
    [InlineData(47.6062, -181.0)]   // Invalid longitude < -180
    public async Task GetNearbyRestaurants_InvalidCoordinates_Returns400(double latitude, double longitude)
    {
        // Act
        var response = await _client.GetAsync($"/api/restaurants/nearby?latitude={latitude}&longitude={longitude}");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetNearbyRestaurants_MissingLatitude_Returns400()
    {
        // Act
        var response = await _client.GetAsync("/api/restaurants/nearby?longitude=-122.3321");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetNearbyRestaurants_MissingLongitude_Returns400()
    {
        // Act
        var response = await _client.GetAsync("/api/restaurants/nearby?latitude=47.6062");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(-1)]   // Limit < 1
    [InlineData(0)]    // Limit = 0
    [InlineData(51)]   // Limit > 50
    [InlineData(100)]  // Limit way over 50
    public async Task GetNearbyRestaurants_InvalidLimit_ShouldReturn400(int limit)
    {
        // Arrange
        var latitude = 47.6062;
        var longitude = -122.3321;

        // Act
        var response = await _client.GetAsync(
            $"/api/restaurants/nearby?latitude={latitude}&longitude={longitude}&limit={limit}");

        // Assert
        _output.WriteLine($"Limit {limit}: Status {response.StatusCode}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problemDetails = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        Assert.NotNull(problemDetails);
        Assert.Contains("limit", problemDetails.Detail ?? "", StringComparison.OrdinalIgnoreCase);
    }
}
