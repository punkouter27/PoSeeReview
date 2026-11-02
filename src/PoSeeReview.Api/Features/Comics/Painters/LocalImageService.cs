using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace PoSeeReview.Api.Features.Comics.Painters;

/// <summary><c>LocalImage</c> configuration for <see cref="LocalImageService"/>.</summary>
public sealed class LocalImageOptions
{
    public const string SectionName = "LocalImage";

    /// <summary>An OpenAI-compatible base URL, ending in <c>/v1</c>.</summary>
    public string BaseUrl { get; set; } = "http://localhost:8080/v1";

    /// <summary>Whatever model name the local server exposes (LocalAI calls it by its YAML name).</summary>
    public string Model { get; set; } = "sdxl-turbo";

    /// <summary>Landscape size for a single-panel strip; a two-panel strip swaps it to portrait.</summary>
    public string LandscapeSize { get; set; } = "768x512";

    /// <summary>A consumer GPU takes tens of seconds for a strip; a CPU, minutes.</summary>
    public int TimeoutSeconds { get; set; } = 300;
}

/// <summary>
/// Draws through a local OpenAI-compatible image server (LocalAI, stable-diffusion.cpp's server,
/// a ComfyUI wrapper) running SDXL-Turbo or FLUX.1-schnell, when <c>Ai:ImageProvider</c> is
/// <c>Local</c>.
/// <para>
/// <b>Why it exists.</b> The chat half of the pipeline already had a free local tier (Ollama), so a
/// scorer prompt could be tuned for nothing — but every image-prompt tweak cost a paid call. This
/// closes that gap. Development only: registration refuses it anywhere else, because a local
/// model's output is not what the product publishes.
/// </para>
/// </summary>
public sealed class LocalImageService(
    IHttpClientFactory httpClientFactory,
    IOptions<LocalImageOptions> options,
    ILogger<LocalImageService> logger) : IImageGenerationService
{
    public const string HttpClientName = "LocalImageApi";

    /// <inheritdoc />
    public string Model => options.Value.Model;

    /// <inheritdoc />
    public async Task<byte[]> GenerateComicImageAsync(IReadOnlyList<string> panelScenes, CancellationToken cancellationToken = default)
    {
        if (panelScenes.Count is < 1 or > 2)
            throw new ArgumentException("Panel count must be 1 or 2", nameof(panelScenes));

        var settings = options.Value;
        var (prompt, _) = ComicImagePrompt.Build(panelScenes);
        var size = panelScenes.Count == 1 ? settings.LandscapeSize : string.Join('x', settings.LandscapeSize.Split('x').Reverse());

        var stopwatch = Stopwatch.StartNew();
        using var response = await httpClientFactory.CreateClient(HttpClientName).PostAsJsonAsync(
            $"{settings.BaseUrl.TrimEnd('/')}/images/generations",
            new { model = settings.Model, prompt, size, n = 1, response_format = "b64_json" },
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Local image server returned {(int)response.StatusCode}: {body}");
        }

        using var json = JsonDocument.Parse(body);
        var image = OpenAiImageResponse.FirstImage(json.RootElement, "Local image server");

        logger.LogInformation("Local image ({Model}, {Size}) drawn in {Duration}ms", settings.Model, size, stopwatch.ElapsedMilliseconds);
        return image;
    }
}
