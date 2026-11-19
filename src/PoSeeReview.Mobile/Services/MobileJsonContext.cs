using System.Text.Json;
using System.Text.Json.Serialization;
using PoSeeReview.Shared.Dtos;

namespace PoSeeReview.Mobile.Services;

/// <summary>The last nearby search, kept for offline use.</summary>
public sealed class SavedNearby
{
    public string Label { get; set; } = string.Empty;
    public NearbyRestaurantsResponse Response { get; set; } = new();
    public Dictionary<string, int> Scores { get; set; } = [];
    public Dictionary<string, int> Forecasts { get; set; } = [];

    /// <summary>Forecasts below this are too ordinary to draw (the server's own cut-off).</summary>
    public int ForecastFloor { get; set; }

    public DateTimeOffset SavedAt { get; set; }
}

/// <summary>Source-generated JSON (trim-safe in Release), web defaults like the API.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(AuthStateDto))]
[JsonSerializable(typeof(NearbyRestaurantsResponse))]
[JsonSerializable(typeof(CachedComicsResponse))]
[JsonSerializable(typeof(ComicDto))]
[JsonSerializable(typeof(ComicGenerationEventDto))]
[JsonSerializable(typeof(LeaderboardResponse))]
[JsonSerializable(typeof(ShareLinkDto))]
[JsonSerializable(typeof(SavedNearby))]
internal sealed partial class MobileJsonContext : JsonSerializerContext;
