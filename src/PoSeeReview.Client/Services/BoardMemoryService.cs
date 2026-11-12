using System.Text.Json;
using Microsoft.JSInterop;

namespace PoSeeReview.Client.Services;

/// <summary>
/// Remembers where each restaurant sat on the leaderboard the last time this browser looked, so
/// the board can say what has CHANGED rather than only what is true.
/// <para>
/// The live board churns — comics expire in 24 hours and the ranking turns over with them — and
/// a returning visitor previously had no way to tell a board that had completely reshuffled from
/// one that had not moved at all. A rank on its own is a fact; a rank next to where it was is the
/// only thing on this page that rewards coming back.
/// </para>
/// <para>
/// Client-side and per-browser, on exactly the terms <see cref="ComicHistoryService"/> is: it
/// needs no account and no server store, it is worthless to anyone else, and it never leaves the
/// device. Every method degrades to a no-op — <c>localStorage</c> throws in private modes and
/// blocked-cookie configurations, and a "moved up 2" badge is never worth taking a page down for.
/// </para>
/// </summary>
public sealed class BoardMemoryService(IJSRuntime js)
{
    /// <summary>One entry per region, so switching country does not report every row as new.</summary>
    private const string StorageKeyPrefix = "poseereview:board-ranks:";

    /// <summary>
    /// Rows kept per region. The board itself shows ten; keeping a few more means a row that
    /// dropped off the bottom and came back is still recognised as a return rather than as new.
    /// </summary>
    private const int MaxTracked = 25;

    private static string KeyFor(string region) => StorageKeyPrefix + region;

    /// <summary>
    /// Reads the remembered ranks for a region. An empty map means "first visit", which callers
    /// must treat as "report nothing" — a first visit where every row claims to be new would be
    /// ten cues at once and would say nothing about change.
    /// </summary>
    public async Task<Dictionary<string, int>> GetAsync(string region)
    {
        try
        {
            var raw = await js.InvokeAsync<string?>("localStorage.getItem", KeyFor(region));
            if (string.IsNullOrWhiteSpace(raw))
            {
                return [];
            }

            return JsonSerializer.Deserialize(raw, AppJsonContext.Default.DictionaryStringInt32) ?? [];
        }
        catch (JSException)
        {
            return [];
        }
        catch (JsonException)
        {
            // A value written by an older shape, or corrupted. Treated as a first visit.
            return [];
        }
        catch (InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>
    /// Records the board as it stands. Call AFTER the deltas have been computed and shown, or
    /// the comparison is against the state the user is currently looking at and every row reads
    /// as unchanged.
    /// </summary>
    public async Task SaveAsync(string region, IEnumerable<(string PlaceId, int Rank)> entries)
    {
        try
        {
            var map = new Dictionary<string, int>();
            foreach (var (placeId, rank) in entries.Take(MaxTracked))
            {
                map[placeId] = rank;
            }

            var json = JsonSerializer.Serialize(map, AppJsonContext.Default.DictionaryStringInt32);
            await js.InvokeVoidAsync("localStorage.setItem", KeyFor(region), json);
        }
        catch (JSException)
        {
            // Storage unavailable. The board renders identically; it simply cannot report change
            // on the next visit.
        }
        catch (InvalidOperationException)
        {
        }
    }
}
