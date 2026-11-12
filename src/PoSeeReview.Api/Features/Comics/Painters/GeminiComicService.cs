using PoSeeReview.Api.Telemetry;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace PoSeeReview.Api.Features.Comics.Painters;

/// <summary>
/// Google Gemini image generation service (generateContent image models).
/// Uses the Generative Language REST API.
/// Requires <c>Google:GeminiApiKey</c> in configuration (stored as "PoSeeReview--Google--GeminiApiKey" in Key Vault).
/// Model must expose <c>generateContent</c>; the Imagen <c>predict</c> family is not available on this key.
/// </summary>
public sealed class GeminiComicService : IImageGenerationService
{
    /// <summary>Telemetry label, and the provider key cost tracking is reported under.</summary>
    public const string ProviderLabel = "Gemini";

    // Verified against ListModels for this project's key on 2026-08-25: NO model exposes the
    // Imagen ":predict" method any more, which is why every generation was failing with
    //   404 "models/imagen-4.0-fast-generate-001 is not found for API version v1beta,
    //        or is not supported for predict"
    // The six image-capable models all expose ":generateContent" instead, returning the image as
    // inline base64 in a candidate part. Overridable via Google:GeminiModel — but any replacement
    // must also be a generateContent image model, not an Imagen predict model.
    private const string DefaultModel = "gemini-2.5-flash-image";
    private const string ApiBase = "https://generativelanguage.googleapis.com/v1beta/models";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GeminiComicService> _logger;
    private readonly TelemetryClient _telemetryClient;
    private readonly AiCostTracker _costTracker;
    private readonly string _apiKey;
    private readonly string _model;

    /// <inheritdoc />
    public string Model => _model;

    public GeminiComicService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<GeminiComicService> logger,
        TelemetryClient telemetryClient,
        AiCostTracker costTracker)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _telemetryClient = telemetryClient ?? throw new ArgumentNullException(nameof(telemetryClient));
        _costTracker = costTracker ?? throw new ArgumentNullException(nameof(costTracker));

        _apiKey = configuration["Google:GeminiApiKey"]
            ?? throw new InvalidOperationException(
                "Google:GeminiApiKey is not configured. " +
                "Add 'PoSeeReview--Google--GeminiApiKey' to Key Vault.");

        _model = configuration["Google:GeminiModel"] ?? DefaultModel;

        _logger.LogInformation("GeminiComicService initialised. Model: {Model} (generateContent image API)", _model);
    }

    /// <inheritdoc />
    public async Task<byte[]> GenerateComicImageAsync(IReadOnlyList<string> panelScenes, CancellationToken cancellationToken = default)
    {
        var panelCount = panelScenes.Count;
        if (panelCount is < 1 or > 2)
            throw new ArgumentException("Panel count must be 1 or 2", nameof(panelScenes));

        var stopwatch = Stopwatch.StartNew();
        var prompt = ComicImagePrompt.Build(panelScenes);

        // One attempt. The "GeminiApi" HttpClient carries a timeout and nothing else: a re-sent
        // image POST can be billed again, so a slow or failed call is surfaced, never retried.
        byte[] imageBytes;
        try
        {
            // The frame shape that makes the layout's panels come out landscape: the overlay
            // places captions by assuming equal panels.
            imageBytes = await GenerateAsync(prompt, panelCount == 1 ? "4:3" : "3:4", cancellationToken);
        }
        catch (ImageDeclinedException ex)
        {
            // A refusal is NOT answered with a generic prompt, and that reversal is the point:
            // paying for a second image of a cheerful restaurant and publishing it under these
            // reviews' score made the product lie. Refusing is the honest outcome, and the one a
            // moderator can act on.
            _telemetryClient.GetMetric("Gemini.Image.Declined").TrackValue(1);
            _logger.LogWarning("Gemini declined to draw this comic: {Reason}", ex.Reason);
            throw;
        }

        stopwatch.Stop();

        _telemetryClient.GetMetric("Gemini.Image.Requests").TrackValue(1);
        _telemetryClient.GetMetric("Gemini.Image.DurationMs").TrackValue(stopwatch.Elapsed.TotalMilliseconds);

        _logger.LogInformation(
            "Generated Gemini comic image ({PanelCount} panels, {Model}) in {Duration}ms, {Size} bytes",
            panelCount, _model, stopwatch.Elapsed.TotalMilliseconds, imageBytes.Length);

        return imageBytes;
    }

    /// <summary>
    /// Calls <c>:generateContent</c> and extracts the inline image bytes from the first image
    /// part of the first candidate.
    /// <para>
    /// The response shape is a candidate list rather than Imagen's <c>predictions</c> array, and
    /// a candidate can legitimately come back with only text parts (the model explaining why it
    /// declined) — so the part loop looks for <c>inlineData</c> specifically instead of assuming
    /// position 0 is the image.
    /// </para>
    /// <para>
    /// <b>A refusal is only ever read from a successful response.</b> An HTTP error is a
    /// failure, whatever words its body contains: matching "blocked" or "safety" in a 403 or
    /// 429 body used to file a quota error as a content refusal and flag the restaurant.
    /// </para>
    /// </summary>
    private static readonly string[] SafetyCategories =
        ["HARM_CATEGORY_HARASSMENT", "HARM_CATEGORY_HATE_SPEECH", "HARM_CATEGORY_SEXUALLY_EXPLICIT", "HARM_CATEGORY_DANGEROUS_CONTENT"];
    private static readonly string[] ImageResponseModalities = ["IMAGE"];

    private async Task<byte[]> GenerateAsync(string prompt, string aspectRatio, CancellationToken cancellationToken)
    {
        var body = new
        {
            contents = new[]
            {
                new { role = "user", parts = new[] { new { text = prompt } } }
            },
            // The adjustable filters all the way open: the owner wants the comic drawn as the
            // reviews describe it. Google's fixed image-safety check still applies; when it
            // refuses, the refusal is shown and reported (ImageDeclinedException), never hidden.
            safetySettings = SafetyCategories
                .Select(category => new { category, threshold = "BLOCK_NONE" }),
            generationConfig = new
            {
                // Without this the model may answer with prose about the picture it would draw.
                responseModalities = ImageResponseModalities,
                imageConfig = new { aspectRatio }
            }
        };

        var client = _httpClientFactory.CreateClient("GeminiApi");
        var url = $"{ApiBase}/{_model}:generateContent";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("x-goog-api-key", _apiKey);
        request.Content = JsonContent.Create(body);

        _logger.LogDebug("Calling Gemini image API: {Url}", url);

        using var response = await client.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Gemini image API error {Status}: {Body}", (int)response.StatusCode, errorBody);
            throw new HttpRequestException(
                $"Gemini image API returned {(int)response.StatusCode}", inner: null, response.StatusCode);
        }

        using var json = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);

        var root = json.RootElement;

        if (root.TryGetProperty("usageMetadata", out var usage))
        {
            _costTracker.Track(ProviderLabel, _model, TokenCount(usage, "promptTokenCount"), TokenCount(usage, "candidatesTokenCount"));
        }

        // A prompt rejected outright never reaches the candidate list — it comes back as a
        // promptFeedback block, which is a refusal by definition.
        if (root.TryGetProperty("promptFeedback", out var feedback)
            && feedback.TryGetProperty("blockReason", out var blockReason))
        {
            throw new ImageDeclinedException($"Gemini blocked the prompt: {blockReason.GetString()}");
        }

        if (!root.TryGetProperty("candidates", out var candidates)
            || candidates.ValueKind != JsonValueKind.Array
            || candidates.GetArrayLength() == 0)
        {
            _logger.LogWarning("Gemini returned no candidates. Raw response: {Response}", root.GetRawText());
            throw new InvalidOperationException("Gemini returned no candidates.");
        }

        var candidate = candidates[0];

        if (candidate.TryGetProperty("finishReason", out var finishReason)
            && finishReason.GetString() is { } reason
            && reason is not ("STOP" or "MAX_TOKENS"))
        {
            // SAFETY / IMAGE_SAFETY / PROHIBITED_CONTENT / BLOCKLIST / SPII / RECITATION are the
            // model declining; anything else (OTHER, NO_IMAGE, ...) is a failed call.
            throw IsRefusalReason(reason)
                ? new ImageDeclinedException($"Gemini declined to draw the image: {reason}")
                : new InvalidOperationException($"Gemini stopped without an image: {reason}");
        }

        if (candidate.TryGetProperty("content", out var content)
            && content.TryGetProperty("parts", out var parts)
            && parts.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("inlineData", out var inlineData)
                    && inlineData.TryGetProperty("data", out var data)
                    && data.GetString() is { Length: > 0 } base64)
                {
                    return Convert.FromBase64String(base64);
                }
            }
        }

        _logger.LogWarning("Gemini returned a candidate with no image part. Raw response: {Response}", root.GetRawText());
        throw new InvalidOperationException("Gemini returned no image data for this prompt.");
    }

    internal static bool IsRefusalReason(string finishReason) =>
        finishReason.Contains("SAFETY", StringComparison.Ordinal)
        || finishReason.Contains("PROHIBITED", StringComparison.Ordinal)
        || finishReason.Contains("RECITATION", StringComparison.Ordinal)
        || finishReason is "BLOCKLIST" or "SPII";

    private static long TokenCount(JsonElement usage, string name) =>
        usage.TryGetProperty(name, out var value) && value.TryGetInt64(out var count) ? count : 0;
}
