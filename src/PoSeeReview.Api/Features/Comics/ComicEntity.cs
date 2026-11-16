using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using PoSeeReview.Shared.Contracts;
using PoSeeReview.Shared.Enums;
using PoSeeReview.Shared.Ids;
// The entity exposes a string column also called PlaceId, which would shadow the id type
// inside ToDomain(); the alias keeps both readable.
using PlaceIdentifier = PoSeeReview.Shared.Ids.PlaceId;

namespace PoSeeReview.Api.Features.Comics;

/// <summary>
/// Table Storage entity for <see cref="Comic"/> with 24-hour cache support.
/// PartitionKey: <see cref="PartitionKeyValue"/>; RowKey: the Google Maps place id.
/// The Table SDK only persists primitives, so the strongly-typed ids are unwrapped here and
/// re-wrapped in <see cref="ToDomain"/> — this is the single storage boundary that sees raw
/// strings (NET_RULES 1.5).
/// </summary>
public class ComicEntity : ITableEntity
{
    /// <summary>Single partition holding every comic row.</summary>
    public const string PartitionKeyValue = "COMIC";

    public string PartitionKey { get; set; } = PartitionKeyValue;
    public string RowKey { get; set; } = string.Empty; // PlaceId
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    // Comic properties
    public string Id { get; set; } = string.Empty;
    public string PlaceId { get; set; } = string.Empty;
    public string RestaurantName { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
    public string Narrative { get; set; } = string.Empty;
    public int StrangenessScore { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string RequestedByUserId { get; set; } = string.Empty;

    /// <summary>
    /// The prompt version the comic was drawn under, and part of the cache key. Absent on older
    /// rows, which reads back as 0 and therefore never matches a configured version — so a row
    /// written before versioning loses the cache exactly once, and regenerates.
    /// </summary>
    public int PromptVersion { get; set; }

    /// <summary>
    /// Panel captions as a JSON array. JSON rather than a joined string because a caption is
    /// free model text and can contain any separator. Absent on older rows, which read back as
    /// no captions.
    /// </summary>
    public string CaptionsJson { get; set; } = string.Empty;

    /// <summary>Panel scenes as a JSON array, for the same reason as <see cref="CaptionsJson"/>.</summary>
    public string ScenesJson { get; set; } = string.Empty;

    /// <summary>The scorer's ratings. Null on older rows, which have no breakdown.</summary>
    public int? Absurdity { get; set; }
    public int? Specificity { get; set; }
    public int? StoryPotential { get; set; }

    /// <summary>Apartment rubric only; null on restaurant and older rows.</summary>
    public int? Horror { get; set; }

    /// <summary>"restaurant" or "apartment"; absent on rows drawn before apartments, which read as restaurant.</summary>
    public string? Category { get; set; }

    /// <summary>Per-panel source reviews as JSON. Absent on older rows.</summary>
    public string SourcesJson { get; set; } = string.Empty;

    public string ImageModel { get; set; } = string.Empty;

    /// <summary><see cref="Shared.Enums.RemasterState"/> by name; absent reads as None.</summary>
    public string RemasterState { get; set; } = string.Empty;

    /// <summary>
    /// Converts from domain <see cref="Comic"/> to the Table Storage entity.
    /// </summary>
    public static ComicEntity FromDomain(Comic comic)
    {
        return new ComicEntity
        {
            PartitionKey = PartitionKeyValue,
            RowKey = comic.PlaceId.Value,
            Id = comic.Id.Value,
            PlaceId = comic.PlaceId.Value,
            RestaurantName = comic.RestaurantName,
            ImageUrl = comic.ImageUrl,
            Narrative = comic.Narrative,
            StrangenessScore = comic.StrangenessScore,
            ExpiresAt = comic.ExpiresAt,
            CreatedAt = comic.CreatedAt,
            RequestedByUserId = comic.RequestedByUserId.Value,
            PromptVersion = comic.PromptVersion,
            CaptionsJson = comic.Captions.Length == 0 ? string.Empty : JsonSerializer.Serialize(comic.Captions),
            ScenesJson = comic.Scenes.Length == 0 ? string.Empty : JsonSerializer.Serialize(comic.Scenes),
            Absurdity = comic.Breakdown?.Absurdity,
            Specificity = comic.Breakdown?.Specificity,
            StoryPotential = comic.Breakdown?.StoryPotential,
            Horror = comic.Breakdown is { Horror: > 0 } b ? b.Horror : null,
            Category = comic.Category.ToWire(),
            SourcesJson = comic.Sources.Length == 0 ? string.Empty : JsonSerializer.Serialize(comic.Sources),
            ImageModel = comic.ImageModel,
            RemasterState = comic.RemasterState == Shared.Enums.RemasterState.None ? string.Empty : comic.RemasterState.ToString()
        };
    }

    /// <summary>
    /// Converts from the Table Storage entity back to the domain <see cref="Comic"/>.
    /// </summary>
    public Comic ToDomain()
    {
        return new Comic
        {
            Id = ComicId.From(Id),
            PlaceId = PlaceIdentifier.From(PlaceId),
            RestaurantName = RestaurantName,
            ImageUrl = ImageUrl,
            Narrative = Narrative,
            StrangenessScore = StrangenessScore,
            ExpiresAt = ExpiresAt,
            CreatedAt = CreatedAt,
            RequestedByUserId = UserId.From(RequestedByUserId),
            PromptVersion = PromptVersion,
            Captions = ReadStrings(CaptionsJson),
            Scenes = ReadStrings(ScenesJson),
            Breakdown = Absurdity is { } a && Specificity is { } s && StoryPotential is { } p
                ? new ComicScoreBreakdown(a, s, p, Horror ?? 0)
                : null,
            Category = PlaceCategories.FromWire(Category),
            Sources = ReadJson<ComicSource[]>(SourcesJson) ?? [],
            ImageModel = ImageModel,
            RemasterState = Enum.TryParse<RemasterState>(RemasterState, out var remaster) ? remaster : Shared.Enums.RemasterState.None,
            // Cache provenance is a service-layer concern; storage never knows it.
            CacheState = ComicCacheState.Generated
        };
    }

    /// <summary>A corrupt column costs the alt text, never the comic.</summary>
    private static string[] ReadStrings(string json) => ReadJson<string[]>(json) ?? [];

    private static T? ReadJson<T>(string json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
