using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.JSInterop;
using PoSeeReview.Shared.Dtos;

namespace PoSeeReview.Client.Services;

/// <summary>
/// One comic the user has seen, as remembered by their browser.
/// </summary>
public sealed class ComicHistoryEntry
{
    public string PlaceId { get; set; } = string.Empty;
    public string RestaurantName { get; set; } = string.Empty;
    public int StrangenessScore { get; set; }

    /// <summary>
    /// Thumbnail URL captured at the time. Comic blobs are cleaned up after expiry, so this is
    /// expected to stop resolving — the entry survives and offers a regenerate instead.
    /// </summary>
    public string BlobUrl { get; set; } = string.Empty;

    public DateTimeOffset ViewedAt { get; set; }

    /// <summary>Cache expiry as it stood when the comic was seen.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>True once the comic's cache window has passed and a fresh one must be drawn.</summary>
    [JsonIgnore]
    public bool IsExpired => ExpiresAt <= DateTimeOffset.UtcNow;

    /// <summary>
    /// How close this entry is to being a dead link, as a value the stylesheet can key on.
    /// <para>
    /// Binary expired/not-expired tells the user nothing until the link is already broken, which
    /// on a 24-hour window is most of what this list is about to become. Grading it means the
    /// list itself communicates the deadline: a card that has visibly started to fade is a
    /// prompt to keep or reopen it, which is the return visit the history exists to earn.
    /// </para>
    /// <para>
    /// A string rather than an enum because it is written straight into a data attribute and
    /// read by an attribute selector; an enum would only be converted back to this at the edge.
    /// </para>
    /// </summary>
    [JsonIgnore]
    public string Freshness
    {
        get
        {
            if (IsExpired)
            {
                return "expired";
            }

            // Half the 24-hour cache window. Earlier than that and almost every card in a normal
            // session would render faded, which would make the state mean nothing.
            var remaining = ExpiresAt - DateTimeOffset.UtcNow;
            return remaining < TimeSpan.FromHours(12) ? "fading" : "fresh";
        }
    }
}

/// <summary>
/// The user's own comic history, kept entirely in their browser.
/// <para>
/// The PRD lists saved history as a v1 non-goal, and that call was made when the alternative was
/// accounts and a server-side store. It is not: comics are addressed by place id, so a list of
/// ids in <c>localStorage</c> reconstructs the whole feature with no backend, no schema, and no
/// personal data leaving the device. Because comics expire after 24 hours, an aged entry becomes
/// a prompt to regenerate rather than dead weight — which is the return visit the app otherwise
/// has no reason to earn.
/// </para>
/// <para>
/// Every method degrades to a no-op rather than throwing. <c>localStorage</c> is unavailable in
/// private modes and blocked-cookie configurations, and a history sidebar is never worth taking
/// the page down for.
/// </para>
/// </summary>
public sealed class ComicHistoryService(IJSRuntime jsRuntime, ILogger<ComicHistoryService> logger)
{
    /// <summary>Storage key, sharing the app's existing <c>posee_</c> prefix.</summary>
    private const string StorageKey = "poseereview:comic-history";

    /// <summary>
    /// How many comics are remembered. Bounded because <c>localStorage</c> is a small, synchronous
    /// store shared with everything else the app keeps there.
    /// </summary>
    public const int MaxEntries = 50;

    /// <summary>Reads the history, newest first. Never throws; an unreadable store reads as empty.</summary>
    public async Task<List<ComicHistoryEntry>> GetAllAsync()
    {
        try
        {
            var raw = await jsRuntime.InvokeAsync<string?>("localStorage.getItem", StorageKey);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return [];
            }

            var entries = JsonSerializer.Deserialize(raw, AppJsonContext.Default.ListComicHistoryEntry) ?? [];
            return entries.OrderByDescending(e => e.ViewedAt).ToList();
        }
        catch (Exception ex) when (ex is JsonException or JSException or InvalidOperationException)
        {
            // Corrupt or inaccessible: an empty history is a correct answer, an exception is not.
            logger.LogWarning(ex, "Could not read comic history");
            return [];
        }
    }

    /// <summary>
    /// Records a comic the user has just seen, moving it to the front if it is already there.
    /// De-duplicating on place id is what keeps "opened the same comic six times" from filling
    /// the list.
    /// </summary>
    public async Task RecordAsync(ComicDto comic)
    {
        if (string.IsNullOrWhiteSpace(comic.PlaceId))
        {
            return;
        }

        try
        {
            var entries = await GetAllAsync();
            entries.RemoveAll(e => e.PlaceId == comic.PlaceId);

            entries.Insert(0, new ComicHistoryEntry
            {
                PlaceId = comic.PlaceId,
                RestaurantName = comic.RestaurantName,
                StrangenessScore = comic.StrangenessScore,
                BlobUrl = comic.BlobUrl,
                ViewedAt = DateTimeOffset.UtcNow,
                ExpiresAt = comic.ExpiresAt
            });

            await SaveAsync(entries.Take(MaxEntries).ToList());
        }
        catch (Exception ex) when (ex is JsonException or JSException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Could not record comic history for {PlaceId}", comic.PlaceId);
        }
    }

    /// <summary>Forgets one comic.</summary>
    public async Task RemoveAsync(string placeId)
    {
        var entries = await GetAllAsync();
        if (entries.RemoveAll(e => e.PlaceId == placeId) > 0)
        {
            await SaveAsync(entries);
        }
    }

    /// <summary>Clears the whole history. The only copy is local, so this is genuinely final.</summary>
    public async Task ClearAsync()
    {
        try
        {
            await jsRuntime.InvokeVoidAsync("localStorage.removeItem", StorageKey);
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Could not clear comic history");
        }
    }

    private async Task SaveAsync(List<ComicHistoryEntry> entries)
    {
        try
        {
            var json = JsonSerializer.Serialize(entries, AppJsonContext.Default.ListComicHistoryEntry);
            await jsRuntime.InvokeVoidAsync("localStorage.setItem", StorageKey, json);
        }
        catch (Exception ex) when (ex is JsonException or JSException or InvalidOperationException)
        {
            // Most likely a full or disabled store. Nothing to recover — the history is a
            // convenience, and the comic itself is unaffected.
            logger.LogWarning(ex, "Could not save comic history");
        }
    }
}
