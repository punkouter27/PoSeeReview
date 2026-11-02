using PoSeeReview.Api.Imaging;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace PoSeeReview.Api.Features.Comics;

/// <summary>
/// Draws readable captions onto a generated comic.
/// <para>
/// Image models render lettering as decoration — shapes that look like words — so every caption
/// this app ships is drawn here with a real font, over the top of whatever the model produced.
/// This service no longer talks to a model at all: captions arrive as an argument, which removed
/// a paid call from the critical path after the image already existed.
/// </para>
/// </summary>
public class ComicTextOverlayService : IComicTextOverlayService
{
    private static readonly SKColor CaptionFill = new(255, 255, 180, 245);

    private readonly ILogger<ComicTextOverlayService> _logger;

    public ComicTextOverlayService(ILogger<ComicTextOverlayService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Adds per-panel English caption overlays. Each panel gets its own caption box at the top,
    /// covering any garbled AI-rendered text.
    /// </summary>
    public Task<byte[]> AddTextOverlayAsync(byte[] imageBytes, IReadOnlyList<string> captions, int panelCount, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(captions);

        if (imageBytes == null || imageBytes.Length == 0)
            throw new ArgumentException("Image bytes cannot be empty", nameof(imageBytes));

        cancellationToken.ThrowIfCancellationRequested();

        _logger.LogInformation("Adding per-panel text overlay to {PanelCount}-panel comic", panelCount);

        try
        {
            using var image = SKBitmap.Decode(imageBytes)
                ?? throw new InvalidDataException("The comic image could not be decoded.");
            using var canvas = new SKCanvas(image);
            var panelBounds = GetPanelBounds(image.Width, image.Height, panelCount);
            var drawn = 0;

            for (int i = 0; i < Math.Min(captions.Count, panelBounds.Count); i++)
            {
                if (string.IsNullOrWhiteSpace(captions[i]))
                    continue;

                // Scale the caption font to the panel width so text stays legible whether the
                // image is a small 512px square or a large 4-panel grid — a fixed size rendered
                // captions nearly invisible on full-resolution output.
                var fontSize = Math.Clamp((int)(panelBounds[i].Width / 20f), 16, 48);
                using var font = SkiaText.Font(SkiaText.Caption, fontSize);
                DrawPanelCaption(canvas, captions[i], panelBounds[i], font);
                drawn++;
            }

            // Lossy WebP, not PNG: a painted strip saved as PNG was ~1.1 MB, and every thumbnail,
            // comic view, share card and download paid for it. Skia encodes WebP lossy at any
            // quality below 100.
            using var webp = image.Encode(SKEncodedImageFormat.Webp, 85)
                ?? throw new InvalidOperationException("The comic could not be encoded as WebP.");

            _logger.LogInformation("Drew {Count} panel caption(s) onto comic", drawn);
            return Task.FromResult(webp.ToArray());
        }
        catch (Exception ex)
        {
            // Losing the captions must not lose the comic: the artwork is the expensive part and
            // an unlabelled strip is still the thing the user waited for.
            _logger.LogError(ex, "Failed to add text overlay, returning original image");
            return Task.FromResult(imageBytes);
        }
    }

    /// <summary>
    /// Approximate bounds of each panel, matching the two layouts <see cref="ComicImagePrompt"/>
    /// asks for: one full-frame panel, or two stacked vertically.
    /// </summary>
    private static List<SKRect> GetPanelBounds(int width, int height, int panelCount)
    {
        if (panelCount <= 1)
        {
            return [SKRect.Create(0, 0, width, height)];
        }

        const int Gutter = 6;
        var panelHeight = (height - Gutter) / 2f;
        return [SKRect.Create(0, 0, width, panelHeight), SKRect.Create(0, panelHeight + Gutter, width, panelHeight)];
    }

    /// <summary>
    /// Draws a classic comic-style yellow caption box at the top of the panel.
    /// Covers any garbled AI-rendered text while providing a clean, readable overlay.
    /// </summary>
    private void DrawPanelCaption(SKCanvas canvas, string text, SKRect panelBounds, SKFont font)
    {
        const float Padding = 6;
        var lines = SkiaText.Wrap(text, font, panelBounds.Width - Padding * 2 - 8);
        var textWidth = lines.Max(line => font.MeasureText(line));
        var top = panelBounds.Top + Padding;
        var centreX = panelBounds.MidX;

        var box = new SKRect(
            centreX - textWidth / 2 - Padding,
            top - Padding,
            centreX + textWidth / 2 + Padding,
            top + font.Spacing * lines.Count + Padding);

        using var paint = SkiaText.Paint(CaptionFill);
        canvas.DrawRect(box, paint);
        paint.Color = SKColors.Black;
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = 1.5f;
        canvas.DrawRect(box, paint);

        SkiaText.DrawLines(canvas, lines, centreX, top - font.Metrics.Ascent, SKTextAlign.Center, font, SKColors.Black);

        _logger.LogDebug("Drew panel caption at ({X},{Y}): {Text}", panelBounds.Left, panelBounds.Top, text);
    }
}
