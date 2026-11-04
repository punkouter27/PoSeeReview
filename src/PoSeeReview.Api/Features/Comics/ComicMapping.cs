using PoSeeReview.Shared.Contracts;
using PoSeeReview.Shared.Dtos;
using PoSeeReview.Shared.Enums;

namespace PoSeeReview.Api.Features.Comics;

/// <summary>
/// Domain <see cref="Comic"/> to wire <see cref="ComicDto"/>. Extracted because two call
/// sites project it — the generation stream and the cached read — and a new field would
/// otherwise be easy to add to one and forget in the other. The scorer's ratings and per-panel
/// sources stay on the domain comic and in storage; nothing on the wire reads them.
/// </summary>
internal static class ComicMapping
{
    public static ComicDto ToDto(this Comic comic, bool? isCached = null) => new()
    {
        ComicId = comic.Id.Value,
        PlaceId = comic.PlaceId.Value,
        RestaurantName = comic.RestaurantName,
        Narrative = comic.Narrative,
        StrangenessScore = comic.StrangenessScore,
        BlobUrl = comic.ImageUrl,
        GeneratedAt = comic.CreatedAt,
        ExpiresAt = comic.ExpiresAt,
        IsCached = isCached ?? comic.CacheState == ComicCacheState.Cached,
        Captions = comic.Captions,
        Scenes = comic.Scenes,
        IsRemastered = comic.RemasterState == RemasterState.Remastered
    };
}
