using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PoSeeReview.Api.Features.Comics;
using PoSeeReview.Api.Storage;
using PoSeeReview.Shared.Contracts;
using PoSeeReview.Shared.Ids;
using SkiaSharp;

namespace PoSeeReview.Integration.Services;

/// <summary>
/// The share card is a real Skia render through a native library, so it lives here rather than in
/// the Unit tier, which runs on a CI image nobody has pinned.
/// </summary>
[Trait("Tier", "Integration")]
public sealed class ShareCardTests
{
    [Fact]
    public async Task RenderAsync_ComposesAFullSizePng_EvenWhenTheArtworkIsGone()
    {
        var blobs = new Mock<IBlobStorageService>();
        blobs.Setup(b => b.OpenComicImageStreamAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Stream?)null);

        var png = await new ShareCardService(blobs.Object, NullLogger<ShareCardService>.Instance).RenderAsync(
            Comic("place-1", "A Restaurant With A Name Far Too Long To Fit On One Line Of A Card, Let Alone Two", 87));

        Assert.NotNull(png);
        var info = SKBitmap.DecodeBounds(png);
        Assert.Equal((ShareCardService.CardWidth, ShareCardService.CardHeight), (info.Width, info.Height));
    }

    private static Comic Comic(string placeId, string name, int score) => new()
    {
        PlaceId = PlaceId.From(placeId),
        RestaurantName = name,
        StrangenessScore = score,
        ImageUrl = $"https://example.blob.core.windows.net/comics/{placeId}.webp"
    };
}
