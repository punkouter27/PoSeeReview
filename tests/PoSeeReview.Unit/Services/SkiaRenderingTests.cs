using PoSeeReview.Api.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using PoSeeReview.Api.Features.Comics;
using SkiaSharp;
using Xunit;

namespace PoSeeReview.Unit.Services;

/// <summary>
/// Unit tier on purpose: it is the only tier CI runs, and these are what prove the Skia native
/// and the embedded fonts load on the Linux runner. The overlay swallows every failure and hands
/// the input back, so without this a broken native would ship uncaptioned comics silently.
/// </summary>
[Trait("Tier", "Unit")]
public sealed class SkiaRenderingTests
{
    [Fact]
    public async Task AddTextOverlayAsync_DrawsCaptions_AndEncodesLossyWebp()
    {
        using var blank = new SKBitmap(512, 768);
        blank.Erase(SKColors.White);
        using var png = blank.Encode(SKEncodedImageFormat.Png, 100);
        var input = png.ToArray();

        var output = await new ComicTextOverlayService(NullLogger<ComicTextOverlayService>.Instance)
            .AddTextOverlayAsync(input, ["The soup was sentient and wanted an apology.", "Then it left."], 2);

        Assert.NotSame(input, output);   // the failure path returns the input array itself
        using var codec = SKCodec.Create(new MemoryStream(output));
        Assert.Equal(SKEncodedImageFormat.Webp, codec.EncodedFormat);
        using var decoded = SKBitmap.Decode(output);
        Assert.NotEqual(SKColors.White, decoded.GetPixel(256, 20));   // caption box over the top panel
    }

    [Fact]
    public void Wrap_BreaksOnAnyWhitespace_AndNeverSplitsASurrogatePair()
    {
        using var font = SkiaText.Font(SkiaText.Card, 24);

        Assert.Equal(["Owl", "Cafe"], SkiaText.Wrap("Owl \nCafe", font, font.MeasureText("Owl Cafe") - 1));

        var lines = SkiaText.Wrap(string.Concat(Enumerable.Repeat("😀", 12)), font, font.MeasureText("😀😀"));
        Assert.All(lines, line => Assert.False(char.IsHighSurrogate(line[^1])));
        Assert.Equal(24, lines.Sum(line => line.Length));
    }
}
