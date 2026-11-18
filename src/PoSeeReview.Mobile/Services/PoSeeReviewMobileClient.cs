using System.Globalization;
using System.Net.Http.Json;
using PoSeeReview.Shared.Dtos;

namespace PoSeeReview.Mobile.Services;

public sealed class PoSeeReviewMobileClient(HttpClient http, MobileConfig config, OfflineComicCache cache)
{
    private Uri BuildUri(string relativePath) => new(new Uri(config.BaseUrl + "/"), relativePath.TrimStart('/'));

    public async Task<AuthStateDto?> GetAuthMeAsync(CancellationToken ct = default)
        => await http.GetFromJsonAsync<AuthStateDto>(BuildUri("/auth/me"), ct).ConfigureAwait(false);

    public async Task<NearbyRestaurantsResponse?> GetNearbyAsync(double latitude, double longitude, CancellationToken ct = default)
    {
        var url = string.Format(
            CultureInfo.InvariantCulture,
            "/api/restaurants/nearby?latitude={0:F6}&longitude={1:F6}&limit=10",
            latitude,
            longitude);
        return await http.GetFromJsonAsync<NearbyRestaurantsResponse>(BuildUri(url), ct).ConfigureAwait(false);
    }

    public async Task<ComicDto?> GetCachedComicAsync(string placeId, CancellationToken ct = default)
    {
        using var resp = await http.GetAsync(BuildUri($"/api/comics/{Uri.EscapeDataString(placeId)}"), ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

        return await resp.Content.ReadFromJsonAsync<ComicDto>(ct).ConfigureAwait(false);
    }

    public async Task<string?> DownloadComicFileAsync(string placeId, CancellationToken ct = default)
    {
        using var resp = await http.GetAsync(BuildUri($"/api/comics/{Uri.EscapeDataString(placeId)}/image"), ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

        var mediaType = resp.Content.Headers.ContentType?.MediaType ?? "image/png";
        var ext = mediaType.Contains("webp", StringComparison.OrdinalIgnoreCase) ? "webp" : "png";
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return await cache.SaveComicImageAsync(placeId, bytes, ext, ct).ConfigureAwait(false);
    }

    public async Task<LeaderboardResponse?> GetLeaderboardAsync(CancellationToken ct = default)
    {
        if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
        {
            return await cache.LoadLeaderboardAsync(ct).ConfigureAwait(false);
        }

        try
        {
            var board = await http.GetFromJsonAsync<LeaderboardResponse>(BuildUri("/api/leaderboard"), ct).ConfigureAwait(false);
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
