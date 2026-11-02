using Microsoft.Extensions.Options;
using PoSeeReview.Shared.Contracts;
using PoSeeReview.Shared.Ids;

namespace PoSeeReview.Api.Features.Comics;

/// <summary>
/// A comic is live when it has not expired and was drawn under the prompt version in force —
/// the same rule <see cref="ComicGenerationService"/> uses to decide a cache hit. Its own type
/// rather than a face of that service, which already depends on the leaderboard.
/// </summary>
public sealed class LiveComicLookup(IComicRepository comics, IOptions<ComicOptions> options, TimeProvider timeProvider)
    : ILiveComicLookup
{
    public async Task<bool> IsLiveAsync(PlaceId placeId) => await GetLiveAsync(placeId) is not null;

    public async Task<Comic?> GetLiveAsync(PlaceId placeId) =>
        await comics.GetByPlaceIdAsync(placeId) is { } comic
        && IsLive(comic, timeProvider.GetUtcNow(), options.Value.PromptVersion)
            ? comic
            : null;

    internal static bool IsLive(Comic? comic, DateTimeOffset now, int promptVersion) =>
        comic is not null && comic.ExpiresAt > now && comic.PromptVersion == promptVersion;
}
