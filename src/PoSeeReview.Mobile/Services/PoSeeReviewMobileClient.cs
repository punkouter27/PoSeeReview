using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PoSeeReview.Shared.Dtos;
using PoSeeReview.Shared.Enums;

namespace PoSeeReview.Mobile.Services;

public enum ComicState { Ready, NotDrawn, Withheld, SignInNeeded, Unavailable }

public sealed record ComicResult(ComicState State, ComicDto? Comic = null, string? ImagePath = null, bool FromCache = false);

/// <summary>A failed draw, carrying the server's own reason (the stream is a 200 before it can fail).</summary>
public sealed class ComicGenerationException(string message, int status, string? errorType) : Exception(message)
{
    public int Status { get; } = status;
    public string? ErrorType { get; } = errorType;
}

public sealed class PoSeeReviewMobileClient(HttpClient http, MobileConfig config, OfflineComicCache cache)
{
    private static readonly TimeSpan DrawTimeout = TimeSpan.FromMinutes(3);

    // Short links are resolved by reading the 302's Location rather than following it into the SPA.
    private static readonly HttpClient NoRedirect = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };

    private static bool Online => Connectivity.Current.NetworkAccess == NetworkAccess.Internet;

    private Uri BuildUri(string relativePath) => new(new Uri(config.BaseUrl + "/"), relativePath.TrimStart('/'));

    public async Task<AuthStateDto?> GetAuthMeAsync(CancellationToken ct = default)
        => await http.GetFromJsonAsync(BuildUri("/auth/me"), MobileJsonContext.Default.AuthStateDto, ct).ConfigureAwait(false);

    /// <summary>Nearby places with their drawn scores and forecasts; the saved search when offline.</summary>
    public Task<SavedNearby?> GetNearbyAsync(double latitude, double longitude, CancellationToken ct = default)
        => SearchCoreAsync(
            string.Format(CultureInfo.InvariantCulture, "/api/restaurants/nearby?latitude={0:F6}&longitude={1:F6}&limit=10", latitude, longitude),
            "Near you",
            ct);

    public Task<SavedNearby?> SearchAsync(string place, CancellationToken ct = default)
        => SearchCoreAsync($"/api/restaurants/search?location={Uri.EscapeDataString(place)}&limit=10", place, ct);

    private async Task<SavedNearby?> SearchCoreAsync(string url, string label, CancellationToken ct)
    {
        if (!Online)
        {
            return await cache.LoadNearbyAsync(ct).ConfigureAwait(false);
        }

        var response = await http.GetFromJsonAsync(BuildUri(url), MobileJsonContext.Default.NearbyRestaurantsResponse, ct).ConfigureAwait(false)
            ?? new NearbyRestaurantsResponse();
        var scores = await GetScoresAsync(response.Restaurants.Select(r => r.PlaceId), ct).ConfigureAwait(false);
        var saved = new SavedNearby
        {
            Label = label,
            Response = response,
            Scores = scores?.Scores ?? [],
            Forecasts = scores?.Forecasts ?? [],
            ForecastFloor = scores?.ForecastFloor ?? 0,
            SavedAt = DateTimeOffset.UtcNow,
        };
        await cache.SaveNearbyAsync(saved, ct).ConfigureAwait(false);
        return saved;
    }

    /// <summary>The last search saved on the phone (the offline fallback after a failed fetch).</summary>
    public Task<SavedNearby?> GetSavedNearbyAsync(CancellationToken ct = default) => cache.LoadNearbyAsync(ct);

    private async Task<CachedComicsResponse?> GetScoresAsync(IEnumerable<string> placeIds, CancellationToken ct)
    {
        var ids = string.Join(',', placeIds.Distinct(StringComparer.Ordinal));
        if (ids.Length == 0)
        {
            return null;
        }

        try
        {
            return await http.GetFromJsonAsync(BuildUri($"/api/comics/cached?placeIds={Uri.EscapeDataString(ids)}"), MobileJsonContext.Default.CachedComicsResponse, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return null; // Pins without scores beat no pins.
        }
    }

    /// <summary>The live comic (saved for offline as a side effect), or why there is none.</summary>
    public async Task<ComicResult> GetComicAsync(string placeId, CancellationToken ct = default)
    {
        if (!Online)
        {
            return await FromCacheAsync(placeId, ct).ConfigureAwait(false);
        }

        try
        {
            using var resp = await http.GetAsync(BuildUri($"/api/comics/{Uri.EscapeDataString(placeId)}"), ct).ConfigureAwait(false);
            switch (resp.StatusCode)
            {
                case HttpStatusCode.NotFound:
                    cache.Evict(placeId);
                    return new(ComicState.NotDrawn);
                case HttpStatusCode.UnavailableForLegalReasons:
                    cache.Evict(placeId);
                    return new(ComicState.Withheld);
                case HttpStatusCode.Unauthorized:
                    return new(ComicState.SignInNeeded);
            }

            resp.EnsureSuccessStatusCode();
            var comic = await resp.Content.ReadFromJsonAsync(MobileJsonContext.Default.ComicDto, ct).ConfigureAwait(false);
            return comic is null ? new(ComicState.Unavailable) : await SaveWithImageAsync(comic, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return await FromCacheAsync(placeId, ct).ConfigureAwait(false);
        }
    }

    private async Task<ComicResult> FromCacheAsync(string placeId, CancellationToken ct)
        => await cache.LoadComicAsync(placeId, ct).ConfigureAwait(false) is { } saved
            ? new(ComicState.Ready, saved.Comic, saved.ImagePath, FromCache: true)
            : new(ComicState.Unavailable);

    private async Task<ComicResult> SaveWithImageAsync(ComicDto comic, CancellationToken ct)
    {
        // Same-origin image endpoint: the blob's SAS URL is unreachable from a phone in local dev
        // (Azurite on 127.0.0.1) and lapses in production.
        using var img = await http.GetAsync(BuildUri($"/api/comics/{Uri.EscapeDataString(comic.PlaceId)}/image"), ct).ConfigureAwait(false);
        if (!img.IsSuccessStatusCode)
        {
            return new(ComicState.Unavailable);
        }

        var ext = img.Content.Headers.ContentType?.MediaType?.Contains("webp", StringComparison.OrdinalIgnoreCase) == true ? "webp" : "png";
        var bytes = await img.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var path = await cache.SaveComicAsync(comic, bytes, ext, ct).ConfigureAwait(false);
        return new(ComicState.Ready, comic, path);
    }

    /// <summary>
    /// Draws the comic over the SSE stream, reporting each pipeline phase. Paid: never retried here
    /// (a retry pays twice), and not cancelled by leaving the page — the server finishes anyway.
    /// </summary>
    public async Task<ComicResult> DrawComicAsync(string placeId, IProgress<ComicGenerationPhase> progress, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(DrawTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri($"/api/comics/{Uri.EscapeDataString(placeId)}/stream"));
        using var resp = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            return new(ComicState.SignInNeeded);
        }

        if (resp.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new ComicGenerationException("Slow down — you can draw a few comics a minute.", 429, "rate_limited");
        }

        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal) || line.Length <= "data:".Length)
            {
                continue;
            }

            var evt = JsonSerializer.Deserialize(line["data:".Length..].Trim(), MobileJsonContext.Default.ComicGenerationEventDto);
            switch (evt?.Kind)
            {
                case ComicGenerationEventDto.PhaseKind:
                    progress.Report(evt.Phase);
                    break;
                case ComicGenerationEventDto.CompleteKind when evt.Comic is not null:
                    return await SaveWithImageAsync(evt.Comic, timeout.Token).ConfigureAwait(false);
                case ComicGenerationEventDto.ErrorKind:
                    throw new ComicGenerationException(evt.ErrorDetail ?? evt.ErrorTitle ?? "The comic couldn't be drawn.", evt.ErrorStatus, evt.ErrorType);
            }
        }

        throw new ComicGenerationException("The connection dropped while the comic was being drawn. Try again in a moment.", 503, null);
    }

    /// <summary>The comic's short link — the one thing shared, so a phone without the app opens the web page.</summary>
    public async Task<string?> CreateShareLinkAsync(string placeId, CancellationToken ct = default)
    {
        using var resp = await http.PostAsync(BuildUri($"/api/share/{Uri.EscapeDataString(placeId)}"), null, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

        return (await resp.Content.ReadFromJsonAsync(MobileJsonContext.Default.ShareLinkDto, ct).ConfigureAwait(false))?.ShortUrl;
    }

    /// <summary>Turns a /s/{code} link into its place id (null for a dead code, which the server sends to /).</summary>
    public async Task<string?> ResolveShortCodeAsync(string code, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri($"/s/{Uri.EscapeDataString(code)}"));
        request.Headers.UserAgent.ParseAdd("PoSeeReview.Mobile/1.0");
        using var resp = await NoRedirect.SendAsync(request, ct).ConfigureAwait(false);
        var location = resp.Headers.Location?.OriginalString;
        const string prefix = "/comic/";
        var at = location?.IndexOf(prefix, StringComparison.Ordinal) ?? -1;
        return at < 0 ? null : Uri.UnescapeDataString(location![(at + prefix.Length)..]);
    }

    public async Task<LeaderboardResponse?> GetLeaderboardAsync(CancellationToken ct = default)
    {
        if (!Online)
        {
            return await cache.LoadLeaderboardAsync(ct).ConfigureAwait(false);
        }

        try
        {
            var board = await http.GetFromJsonAsync(BuildUri("/api/leaderboard"), MobileJsonContext.Default.LeaderboardResponse, ct).ConfigureAwait(false);
            if (board is not null)
            {
                await cache.SaveLeaderboardAsync(board, ct).ConfigureAwait(false);
            }

            return board;
        }
        catch (HttpRequestException)
        {
            return await cache.LoadLeaderboardAsync(ct).ConfigureAwait(false);
        }
    }
}
