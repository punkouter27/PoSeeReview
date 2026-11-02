using SkiaSharp;

namespace PoSeeReview.Api.Imaging;

/// <summary>
/// Fonts and the little text layout Skia does not do for us (it draws a line, it does not wrap).
/// <para>
/// The typefaces are embedded and are the only ones used. App Service runs the fontconfig-free
/// Linux build of Skia, which cannot be relied on to find any system font, and a render that
/// depended on what a host happened to have installed came out differently on every machine.
/// So text is Latin-only by design: a character the face lacks draws as a box, everywhere alike.
/// These are the same OFL faces the client ships as woff2 in wwwroot/fonts — update both together.
/// </para>
/// </summary>
internal static class SkiaText
{
    /// <summary>Comic lettering for the panel captions — the face the app's own headings use.</summary>
    public static readonly SKTypeface Caption = Load("Bangers-Regular.ttf");

    /// <summary>A neutral bold sans for the share card's wordmark, score and restaurant name.</summary>
    public static readonly SKTypeface Card = Load("DMSans-Bold.ttf");

    private static SKTypeface Load(string file)
    {
        using var stream = typeof(SkiaText).Assembly.GetManifestResourceStream($"Fonts.{file}")
            ?? throw new InvalidOperationException($"Embedded font {file} is missing.");

        // SKData copies the bytes: a typeface made straight from a managed stream reads it lazily
        // and would outlive this using block.
        return SKTypeface.FromData(SKData.Create(stream))
            ?? throw new InvalidOperationException($"Embedded font {file} could not be parsed.");
    }

    public static SKFont Font(SKTypeface typeface, float size) =>
        new(typeface, size) { Edging = SKFontEdging.Antialias, Subpixel = true };

    public static SKPaint Paint(SKColor colour) => new() { Color = colour, IsAntialias = true };

    /// <summary>Greedy word wrap to <paramref name="maxWidth"/>; a word wider than a line is split.</summary>
    public static List<string> Wrap(string text, SKFont font, float maxWidth)
    {
        List<string> lines = [];
        var line = string.Empty;

        // Any whitespace breaks: restaurant names are third-party and arrive with tabs, NBSPs and
        // newlines, and Skia would draw a newline as a box rather than break on it.
        foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = line.Length == 0 ? word : $"{line} {word}";
            if (font.MeasureText(candidate) <= maxWidth)
            {
                line = candidate;
                continue;
            }

            if (line.Length > 0)
            {
                lines.Add(line);
            }

            line = word;
            while (line.Length > 1 && font.MeasureText(line) > maxWidth)
            {
                var fit = Math.Max(1, (int)font.BreakText(line, maxWidth));
                if (char.IsHighSurrogate(line[fit - 1]))
                {
                    // Never strand half of a surrogate pair at a line end.
                    fit = fit > 1 ? fit - 1 : 2;
                }

                lines.Add(line[..fit]);
                line = line[fit..];
            }
        }

        if (line.Length > 0)
        {
            lines.Add(line);
        }

        return lines;
    }

    /// <summary>Lines stepped by the font's own leading, the first on <paramref name="firstBaseline"/>.</summary>
    public static void DrawLines(SKCanvas canvas, IReadOnlyList<string> lines, float x, float firstBaseline, SKTextAlign align, SKFont font, SKColor colour)
    {
        using var paint = Paint(colour);
        for (var i = 0; i < lines.Count; i++)
        {
            canvas.DrawText(lines[i], x, firstBaseline + font.Spacing * i, align, font, paint);
        }
    }

    /// <summary>One line centred on a point, by its ink bounds rather than its font metrics.</summary>
    public static void DrawCentred(SKCanvas canvas, string text, float x, float y, SKTypeface typeface, float size, SKColor colour)
    {
        using var font = Font(typeface, size);
        using var paint = Paint(colour);
        font.MeasureText(text, out var bounds);
        canvas.DrawText(text, x - bounds.MidX, y - bounds.MidY, SKTextAlign.Left, font, paint);
    }
}
