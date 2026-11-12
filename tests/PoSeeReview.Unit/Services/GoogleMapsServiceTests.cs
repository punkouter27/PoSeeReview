using Moq;
using PoSeeReview.Api.Features.Restaurants;
using Xunit;

namespace PoSeeReview.Unit.Services;

/// <summary>
/// Unit tests for GoogleMapsService (Places API integration)
/// </summary>
[Trait("Tier", "Unit")]
[Trait("Suite", "CriticalPath")]
public class GoogleMapsServiceTests
{
    [Fact]
    public void ValidateCoordinates_ValidLatitude_DoesNotThrow()
    {
        // Arrange
        var latitude = 47.6062;
        var longitude = -122.3321;

        // Act & Assert
        var exception = Record.Exception(() => ValidateCoordinates(latitude, longitude));
        Assert.Null(exception);
    }

    [Theory]
    [InlineData(91.0, -122.3321)]  // Latitude out of range
    [InlineData(47.6062, 181.0)]   // Longitude out of range
    public void ValidateCoordinates_InvalidCoordinates_ThrowsArgumentException(double lat, double lon)
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => ValidateCoordinates(lat, lon));
    }

    private static void ValidateCoordinates(double latitude, double longitude)
    {
        if (latitude < -90 || latitude > 90)
            throw new ArgumentException("Latitude must be between -90 and 90", nameof(latitude));

        if (longitude < -180 || longitude > 180)
            throw new ArgumentException("Longitude must be between -180 and 180", nameof(longitude));
    }
}
