namespace PoSeeReview.Api.Features.Comics.Painters;

/// <summary>
/// Service for generating comic strip images using an AI image model: gpt-image via Azure,
/// Gemini, or a local server — selected by <c>Ai:ImageProvider</c> (drafts) and
/// <c>Ai:RemasterProvider</c> (redraws of popular comics).
/// </summary>
public interface IImageGenerationService
{
    /// <summary>The model or deployment that draws; recorded on each comic so a remaster knows what it replaces.</summary>
    string Model { get; }

    /// <summary>
    /// Generates a comic strip image from the panel scenes. One paid attempt: implementations
    /// never retry.
    /// </summary>
    /// <param name="panelScenes">One illustrator brief per panel (1-2); the count is the panel count</param>
    /// <param name="cancellationToken">Cancels the (potentially slow) image generation call</param>
    /// <returns>Encoded image bytes (PNG, JPEG or WebP, whatever the provider returned)</returns>
    /// <exception cref="ArgumentException">If the panel count is invalid</exception>
    /// <exception cref="ImageDeclinedException">If the model refused to draw the scenes</exception>
    Task<byte[]> GenerateComicImageAsync(IReadOnlyList<string> panelScenes, CancellationToken cancellationToken = default);
}
