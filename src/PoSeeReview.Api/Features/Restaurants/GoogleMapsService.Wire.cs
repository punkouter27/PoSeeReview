using System.Text.Json.Serialization;

namespace PoSeeReview.Api.Features.Restaurants;

/// <summary>The Google Places and Geocoding response shapes <see cref="GoogleMapsService"/> reads.</summary>
public partial class GoogleMapsService
{
    // Google Places API (New) response models
    private class GooglePlacesResponse
    {
        [JsonPropertyName("places")]
        public List<Place>? Places { get; set; }
    }

    private class Place
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("displayName")]
        public DisplayName? DisplayName { get; set; }

        [JsonPropertyName("formattedAddress")]
        public string? FormattedAddress { get; set; }

        [JsonPropertyName("location")]
        public Location? Location { get; set; }

        [JsonPropertyName("rating")]
        public double? Rating { get; set; }

        [JsonPropertyName("userRatingCount")]
        public int? UserRatingCount { get; set; }
    }

    private class DisplayName
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }

    private class Location
    {
        [JsonPropertyName("latitude")]
        public double Latitude { get; set; }

        [JsonPropertyName("longitude")]
        public double Longitude { get; set; }
    }

    // Place Details API response models
    private class PlaceDetailsResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("displayName")]
        public DisplayName? DisplayName { get; set; }

        [JsonPropertyName("formattedAddress")]
        public string? FormattedAddress { get; set; }

        [JsonPropertyName("location")]
        public Location? Location { get; set; }

        [JsonPropertyName("rating")]
        public double? Rating { get; set; }

        [JsonPropertyName("userRatingCount")]
        public int? UserRatingCount { get; set; }

        [JsonPropertyName("types")]
        public List<string>? Types { get; set; }

        [JsonPropertyName("reviews")]
        public List<PlaceReview>? Reviews { get; set; }

        // Support both wrapper and direct access patterns
        [JsonPropertyName("place")]
        public PlaceDetailsResponse? Place { get; set; }
    }

    private class PlaceReview
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("relativePublishTimeDescription")]
        public string? RelativePublishTimeDescription { get; set; }

        [JsonPropertyName("rating")]
        public int? Rating { get; set; }

        [JsonPropertyName("text")]
        public TextContent? Text { get; set; }

        [JsonPropertyName("originalText")]
        public TextContent? OriginalText { get; set; }

        [JsonPropertyName("authorAttribution")]
        public AuthorAttribution? AuthorAttribution { get; set; }

        [JsonPropertyName("publishTime")]
        public string? PublishTime { get; set; }
    }

    private class TextContent
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("languageCode")]
        public string? LanguageCode { get; set; }
    }

    private class AuthorAttribution
    {
        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("uri")]
        public string? Uri { get; set; }

        [JsonPropertyName("photoUri")]
        public string? PhotoUri { get; set; }
    }

    private class GeocodeResponse
    {
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("results")]
        public List<GeocodeResult>? Results { get; set; }
    }

    private class GeocodeResult
    {
        [JsonPropertyName("geometry")]
        public GeocodeGeometry? Geometry { get; set; }
    }

    private class GeocodeGeometry
    {
        [JsonPropertyName("location")]
        public Location? Location { get; set; }
    }
}
