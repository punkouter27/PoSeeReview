using PoSeeReview.Shared.Ids;

namespace PoSeeReview.Shared.Contracts;

/// <summary>
/// Whether a place's comic would open for free right now. Owned by Comics, read by Leaderboard
/// and Sharing (NET_RULES 2.2): the live board must only list rows whose tap lands on a comic, not
/// on the paid "Generate" prompt an expired or prompt-version-stale comic falls through to, and a
/// share card is drawn only from a live comic.
/// </summary>
public interface ILiveComicLookup
{
    Task<bool> IsLiveAsync(PlaceId placeId);

    /// <summary>The live comic, or null — the same rule as <see cref="IsLiveAsync"/>.</summary>
    Task<Comic?> GetLiveAsync(PlaceId placeId);
}
