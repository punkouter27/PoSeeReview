using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components.WebAssembly.Http;
using PoSeeReview.Shared.Dtos;
using PoSeeReview.Shared.Enums;

namespace PoSeeReview.Client.Services;

/// <summary>
/// HTTP client for calling the PoSeeReview API backend.
/// </summary>
public class ApiClient
{
    private readonly HttpClient _httpClient;

    public ApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// Gets nearby restaurants based on coordinates.
    /// On non-success the response body is parsed for an RFC 7807 <c>ProblemDetails</c> payload
    /// so the user-facing message is actionable instead of a generic 503.
    /// Returns <c>null</c> only when the body cannot be parsed.
    /// </summary>
    public async Task<NearbyRestaurantsResponse?> GetNearbyRestaurantsAsync(
        double latitude,
        double longitude,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/restaurants/nearby?latitude={latitude}&longitude={longitude}&limit={limit}");
        using var httpResponse = await _httpClient.SendAsync(request, cancellationToken);

        await EnsureSuccessAsync(httpResponse, "Nearby restaurant search failed", cancellationToken);

        return await httpResponse.Content.ReadFromJsonAsync(AppJsonContext.Default.NearbyRestaurantsResponse, cancellationToken);
    }

    /// <summary>
    /// Generates a comic while reporting the pipeline stage the server is genuinely in. Once the
    /// server has answered 200 the paid pipeline is running, so a mid-stream failure is surfaced
    /// as an error rather than retried: a retry there would pay for the same comic twice.
    /// </summary>
    public async Task<ComicDto> GenerateComicStreamAsync(
        string placeId,
        bool forceRegenerate,
        IProgress<ComicGenerationEventDto> progress,
        CancellationToken cancellationToken = default)
    {
        var url = $"/api/comics/{placeId}/stream";
        if (forceRegenerate)
        {
            url += "?forceRegenerate=true";
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, url);

        // Without this the browser HttpClient buffers the entire body before ReadAsStreamAsync
        // yields a byte, so every phase arrived in one burst after `complete` and the stepper
        // never moved. ResponseHeadersRead alone is not enough on WASM.
        request.SetBrowserResponseStreamingEnabled(true);

        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        await EnsureSuccessAsync(response, "Comic generation failed", cancellationToken, "Please try again in a moment.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            // Blank lines are SSE frame separators; anything that is not a data line is a
            // comment or a field this client does not use.
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line["data:".Length..].Trim();
            if (payload.Length == 0)
            {
                continue;
            }

            var evt = JsonSerializer.Deserialize(payload, AppJsonContext.Default.ComicGenerationEventDto);
            if (evt is null)
            {
                continue;
            }

            switch (evt.Kind)
            {
                case ComicGenerationEventDto.PhaseKind:
                    progress.Report(evt);
                    break;

                case ComicGenerationEventDto.CompleteKind when evt.Comic is not null:
                    return evt.Comic;

                case ComicGenerationEventDto.ErrorKind:
                    throw new HttpRequestException(
                        evt.ErrorDetail ?? evt.ErrorTitle ?? "Comic generation failed.",
                        null,
                        (System.Net.HttpStatusCode)evt.ErrorStatus)
                    { Data = { ["errorType"] = evt.ErrorType } };
            }
        }

        // A 200 that ends without a terminal event means the connection dropped mid-pipeline.
        throw new HttpRequestException(
            "The connection dropped while the comic was being drawn. Please try again.",
            null,
            System.Net.HttpStatusCode.ServiceUnavailable);
    }

    /// <summary>
    /// Fetches the cached comic for a place without triggering (paid) generation.
    /// Returns <c>null</c> when no valid cached comic exists (HTTP 404).
    /// </summary>
    public async Task<ComicDto?> GetCachedComicAsync(
        string placeId,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/comics/{placeId}");
        using var response = await _httpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, "Comic lookup failed", cancellationToken);

        return await response.Content.ReadFromJsonAsync(AppJsonContext.Default.ComicDto, cancellationToken);
    }

    /// <summary>The comic's audio, if it has been recorded already (free). Null on a 404.</summary>
    public async Task<AudioComicDto?> GetAudioComicAsync(string placeId, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"/api/comics/{placeId}/audio", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, "Audio lookup failed", cancellationToken);
        return await response.Content.ReadFromJsonAsync(AppJsonContext.Default.AudioComicDto, cancellationToken);
    }

    /// <summary>
    /// Records the comic's audio (paid, a few seconds). A refusal surfaces with its reason: the
    /// owner wants every refusal visible.
    /// </summary>
    public async Task<AudioComicDto> RecordAudioComicAsync(string placeId, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsync($"/api/comics/{placeId}/audio", null, cancellationToken);
        await EnsureSuccessAsync(response, "Recording the audio failed", cancellationToken, "Please try again in a moment.");
        return await response.Content.ReadFromJsonAsync(AppJsonContext.Default.AudioComicDto, cancellationToken)
            ?? throw new InvalidOperationException("Audio response was null");
    }

    /// <summary>
    /// Searches for restaurants by location query (city name or ZIP code).
    /// </summary>
    public async Task<NearbyRestaurantsResponse?> SearchRestaurantsByLocationAsync(
        string locationQuery,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        var url = $"/api/restaurants/search?location={Uri.EscapeDataString(locationQuery)}&limit={limit}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _httpClient.SendAsync(request, cancellationToken);

        await EnsureSuccessAsync(response, "Search request failed", cancellationToken);

        var payload = await response.Content.ReadFromJsonAsync(AppJsonContext.Default.NearbyRestaurantsResponse, cancellationToken);
        return payload;
    }

    /// <summary>
    /// Throws an <see cref="HttpRequestException"/> carrying the API's ProblemDetails
    /// <c>detail</c> when the response is not a success.
    /// <para>
    /// Every call site used to inline this: read body, <see cref="TryExtractProblemDetail"/>,
    /// throw. The copies had already drifted apart in wording, and two endpoints skipped it
    /// entirely for <c>EnsureSuccessStatusCode()</c> — which surfaces the framework's opaque
    /// "net_http_message_not_success_statuscode_reason" text that this helper exists to replace.
    /// </para>
    /// </summary>
    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string fallback,
        CancellationToken cancellationToken,
        string? suffix = null)
    {
        if (response.IsSuccessStatusCode)
            return;

        var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
        var message = TryExtractProblemDetail(errorBody)
            ?? $"{fallback} (HTTP {(int)response.StatusCode}).{(suffix is null ? "" : " " + suffix)}";

        throw new HttpRequestException(message, null, response.StatusCode);
    }

    private static string? TryExtractProblemDetail(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("detail", out var detailElement))
            {
                var detail = detailElement.GetString();
                if (!string.IsNullOrWhiteSpace(detail))
                {
                    return detail;
                }
            }

            if (root.TryGetProperty("title", out var titleElement))
            {
                var title = titleElement.GetString();
                if (!string.IsNullOrWhiteSpace(title))
                {
                    return title;
                }
            }
        }
        catch (JsonException)
        {
            // Response may not be JSON; fall through to null.
        }

        return null;
    }

    /// <summary>
    /// Gets the leaderboard entries for a given region.
    /// </summary>
    public async Task<LeaderboardResponse?> GetLeaderboardAsync(
        string region = "US",
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"/api/leaderboard?region={region}&limit={limit}");
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            await EnsureSuccessAsync(response, "Leaderboard request failed", cancellationToken);
            return await response.Content.ReadFromJsonAsync(AppJsonContext.Default.LeaderboardResponse, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>
    /// The permanent weekly archive, worldwide. Returns <c>null</c> on failure so the page can
    /// say so instead of throwing into the renderer.
    /// </summary>
    public async Task<HallOfFameResponse?> GetWeeklyHallOfFameAsync(
        int weeks = 4,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"/api/leaderboard/weekly?weeks={weeks}&limit={limit}");
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            await EnsureSuccessAsync(response, "Weekly archive request failed", cancellationToken);
            return await response.Content.ReadFromJsonAsync(AppJsonContext.Default.HallOfFameResponse, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>
    /// What the signed-in user has left to spend today. Returns <c>null</c> when the budget
    /// cannot be read, which callers treat as "allow" — a failed read must not lock the app's
    /// primary action.
    /// </summary>
    public async Task<GenerationBudgetDto?> GetGenerationBudgetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/comics/budget");
            using var response = await _httpClient.SendAsync(request, cancellationToken);

            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync(AppJsonContext.Default.GenerationBudgetDto, cancellationToken)
                : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>
    /// Which of these places already have a live comic — the ones that will open instantly and
    /// cost nothing — with each one's strangeness score, plus the weirdness forecast for the ones
    /// that do not. Returns an empty probe on failure, so the map simply shows every pin as undrawn
    /// rather than promising a free result it cannot confirm.
    /// </summary>
    public async Task<CachedComicsResponse> GetComicProbeAsync(
        IEnumerable<string> placeIds,
        CancellationToken cancellationToken = default)
    {
        var ids = placeIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0)
        {
            return new();
        }

        try
        {
            var query = Uri.EscapeDataString(string.Join(',', ids));
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/comics/cached?placeIds={query}");
            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return new();
            }

            return await response.Content.ReadFromJsonAsync(
                AppJsonContext.Default.CachedComicsResponse, cancellationToken) ?? new();
        }
        catch (HttpRequestException)
        {
            return new();
        }
    }

    /// <summary>
    /// Mints (or re-reads) this comic's short link.
    /// <para>
    /// Returns <c>null</c> on any failure, and the caller shares the canonical URL instead. A
    /// share must never fail because the nicer address could not be produced — the long URL
    /// works, it is just uglier.
    /// </para>
    /// </summary>
    public async Task<ShareLinkDto?> CreateShareLinkAsync(
        string placeId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/share/{placeId}");
            using var response = await _httpClient.SendAsync(request, cancellationToken);

            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync(AppJsonContext.Default.ShareLinkDto, cancellationToken)
                : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>
    /// Submits a viewer report. Unlike most calls here this one surfaces its failure: someone
    /// reporting content needs to know whether it was actually received.
    /// </summary>
    public async Task<ComicReportResponseDto> SubmitReportAsync(
        ComicReportRequestDto report,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/reports");
        request.Content = JsonContent.Create(report, AppJsonContext.Default.ComicReportRequestDto);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "Report submission failed", cancellationToken, "Please try again in a moment.");

        var payload = await response.Content.ReadFromJsonAsync(AppJsonContext.Default.ComicReportResponseDto, cancellationToken);
        return payload ?? throw new InvalidOperationException("Report response was null");
    }

    public async Task<HealthStatusDto?> GetHealthStatusAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "Health request failed", cancellationToken);
        return await response.Content.ReadFromJsonAsync(AppJsonContext.Default.HealthStatusDto, cancellationToken);
    }

    public async Task<DiagnosticsSnapshotDto?> GetDiagnosticsSnapshotAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/diag");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "Diagnostics request failed", cancellationToken);
        return await response.Content.ReadFromJsonAsync(AppJsonContext.Default.DiagnosticsSnapshotDto, cancellationToken);
    }
}
