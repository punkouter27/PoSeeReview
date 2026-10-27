using PoSeeReview.Api.Storage;
using SkiaSharp;
using PoSeeReview.Shared.Contracts;

namespace PoSeeReview.Api.Features.Comics;

/// <summary>
/// Builds the link-preview card.
/// <para>
/// This exists because <c>og:image</c> used to point straight at the comic's blob URL, and that
/// URL carries a SAS signature that lapses after about a week while the Hall of Fame entry it
/// came from is designed to outlive everything. Every share older than the signature degraded
/// to a blank card. Serving the image from this origin makes the preview outlive the signature
/// and lets the card carry the score and the wordmark, which the raw comic does not.
/// </para>
/// <para>
/// Composed on demand rather than stored. It is a deterministic function of a comic that already
/// exists, the render is a resize and a handful of draw calls, and caching it would add a second
/// blob lifecycle that takedown would then have to know about.
/// </para>
/// </summary>
public sealed class ShareCardService(
    IBlobStorageService blobStorageService,
    ILogger<ShareCardService> logger)
{
    /// <summary>The size every major link-preview client expects. Anything else gets letterboxed.</summary>
    public const int CardWidth = 1200;
    public const int CardHeight = 630;

    // Brand colours are literals here on purpose. This is a server-rendered PNG: there is no
    // stylesheet to read, no viewer theme to respect, and a card that changed appearance
    // between renders would be a worse artifact, not a more correct one. They mirror
    // --color-brand / --color-brand-dark / --color-accent in app.css.
    private static readonly SKColor BrandDark = SKColor.Parse("#2E1065");
    private static readonly SKColor Accent = SKColor.Parse("#F59E0B");
    private static readonly SKColor OnDark = SKColor.Parse("#F8FAFC");
    private static readonly SKColor OnDarkMuted = SKColor.Parse("#CBD5E1");
    private static readonly SKColor Shade = new(15, 6, 35);

    /// <summary>Height of the bottom band that carries the name and wordmark.</summary>
    private const int ScrimHeight = 190;

    private const int TextMargin = 48;

    public async Task<byte[]?> RenderAsync(Comic comic, CancellationToken cancellationToken = default)
    {
        try
        {
            using var card = new SKBitmap(CardWidth, CardHeight);
            using var canvas = new SKCanvas(card);
            canvas.Clear(BrandDark);

            var area = SKRectI.Create(CardWidth, CardHeight);
            await DrawArtworkAsync(canvas, comic, area, cancellationToken);
            DrawScrim(canvas);
            DrawScoreBadge(canvas, comic.StrangenessScore, area);
            DrawName(canvas, comic.RestaurantName, area);
            DrawWordmark(canvas, "POSEEREVIEW  ·  STRANGENESS SCORE");

            using var png = card.Encode(SKEncodedImageFormat.Png, 100)
                ?? throw new InvalidOperationException("The share card could not be encoded.");
            return png.ToArray();
        }
        catch (Exception ex)
        {
            // A preview is decoration. Failing to build one returns null so the caller can 404
            // and let the crawler fall back to the text tags, which is a worse card but not a
            // broken page.
            logger.LogWarning(ex, "Failed to render the share card for {PlaceId}", comic.PlaceId);
            return null;
        }
    }

    /// <summary>
    /// Draws the comic itself, cropped to fill its column.
    /// <para>
    /// A missing blob is not a failure: the comic may have been cleaned up while its Hall of
    /// Fame row lives on. The card still renders — brand ground, score, name — which is exactly
    /// the case the old blob-URL <c>og:image</c> could not survive.
    /// </para>
    /// </summary>
    private async Task DrawArtworkAsync(SKCanvas canvas, Comic comic, SKRectI area, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(comic.ImageUrl))
        {
            return;
        }

        await using var stream = await blobStorageService.OpenComicImageStreamAsync(comic.ImageUrl, cancellationToken);
        if (stream is null)
        {
            logger.LogInformation("Share card for {PlaceId} has no artwork; rendering the text-only card", comic.PlaceId);
            return;
        }

        // Skia's decoder wants to seek; a blob download stream cannot.
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;
        using var artwork = SKImage.FromEncodedData(buffer);
        if (artwork is null)
        {
            // Same outcome as a missing blob: the text-only card beats a 404.
            logger.LogWarning("Share card artwork for {PlaceId} could not be decoded; rendering the text-only card", comic.PlaceId);
            return;
        }

        // Crop rather than pad: a comic letterboxed onto brand purple reads as a mistake, and the
        // panels nearest the centre are the ones worth showing in a thumbnail.
        var cover = Math.Max(area.Width / (float)artwork.Width, area.Height / (float)artwork.Height);
        var source = SKRect.Create(area.Width / cover, area.Height / cover);
        source.Offset((artwork.Width - source.Width) / 2f, (artwork.Height - source.Height) / 2f);

        canvas.DrawImage(artwork, source, area, new SKSamplingOptions(SKCubicResampler.Mitchell));
    }

    /// <summary>
    /// A darkening band across the bottom third so the caption is readable over any artwork.
    /// <para>
    /// A quadratic ramp rather than a flat translucent rectangle: a flat overlay has a hard top
    /// edge that reads as a bug on a light comic panel.
    /// </para>
    /// </summary>
    private static void DrawScrim(SKCanvas canvas)
    {
        float[] stops = [0f, 0.25f, 0.5f, 0.75f, 1f];
        using var paint = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, CardHeight - ScrimHeight),
                new SKPoint(0, CardHeight),
                [.. stops.Select(t => Shade.WithAlpha((byte)(235 * t * t)))],
                stops,
                SKShaderTileMode.Clamp)
        };
        canvas.DrawRect(SKRect.Create(0, CardHeight - ScrimHeight, CardWidth, ScrimHeight), paint);
        paint.Shader = null;

        // A brand rule along the very bottom, so the card is recognisable at thumbnail size
        // even when the artwork is missing.
        paint.Color = Accent;
        canvas.DrawRect(SKRect.Create(0, CardHeight - 8, CardWidth, 8), paint);
    }

    private static void DrawScoreBadge(SKCanvas canvas, int score, SKRectI area)
    {
        const int Margin = 40;
        const float Radius = 84;
        var centre = new SKPoint(area.Right - Margin - Radius, Margin + Radius);

        DrawDisc(canvas, centre, Radius, Shade.WithAlpha(200), 6f);

        SkiaText.DrawCentred(canvas, score.ToString(), centre.X, centre.Y - 12, SkiaText.Card, 74, Accent);
        SkiaText.DrawCentred(canvas, "/ 100", centre.X, centre.Y + 44, SkiaText.Card, 20, OnDarkMuted);
    }

    private static void DrawName(SKCanvas canvas, string restaurantName, SKRectI area)
    {
        // Third-party text of unbounded length. Wrapped beside the score badge, then hard-capped
        // so a pathological name cannot push the wordmark off the bottom edge.
        const int MaxChars = 70;
        var name = restaurantName.Trim();
        if (name.Length > MaxChars)
        {
            name = name[..(MaxChars - 1)].TrimEnd() + "…";
        }

        using var font = SkiaText.Font(SkiaText.Card, 52);
        var lines = SkiaText.Wrap(name, font, area.Width - (TextMargin * 2) - 200);
        DrawBottomAligned(canvas, lines, area.Left + TextMargin, CardHeight - TextMargin - 44, font, OnDark);
    }

    private static void DrawWordmark(SKCanvas canvas, string wordmark)
    {
        using var font = SkiaText.Font(SkiaText.Card, 24);
        DrawBottomAligned(canvas, [wordmark], TextMargin, CardHeight - TextMargin - 8, font, Accent);
    }

    private static void DrawDisc(SKCanvas canvas, SKPoint centre, float radius, SKColor fill, float ringWidth)
    {
        using var paint = SkiaText.Paint(fill);
        canvas.DrawCircle(centre, radius, paint);

        paint.Color = Accent;
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = ringWidth;
        canvas.DrawCircle(centre, radius, paint);
    }

    /// <summary>Left-aligned lines whose last descender sits on <paramref name="bottom"/>.</summary>
    private static void DrawBottomAligned(SKCanvas canvas, IReadOnlyList<string> lines, float left, float bottom, SKFont font, SKColor colour) =>
        SkiaText.DrawLines(canvas, lines, left, bottom - font.Metrics.Descent - font.Spacing * (lines.Count - 1), SKTextAlign.Left, font, colour);
}
