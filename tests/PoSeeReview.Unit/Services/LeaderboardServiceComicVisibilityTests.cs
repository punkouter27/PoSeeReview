using PoSeeReview.Shared.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using PoSeeReview.Api.Features.Leaderboard;
using PoSeeReview.Api.Storage;
using PoSeeReview.Shared.Contracts;
using PoSeeReview.Shared.Ids;

namespace PoSeeReview.Unit.Services;

/// <summary>
/// The Hall of Fame only paints rows that still have a comic image. Entries whose blob was
/// purged (or never stored) must not occupy a rank.
/// </summary>
[Trait("Tier", "Unit")]
[Trait("Suite", "CriticalPath")]
public sealed class LeaderboardServiceComicVisibilityTests
{
    [Fact]
    public async Task GetTopComicsAsync_OmitsEntriesWithoutAComicBlob()
    {
        var region = RegionCode.From("US");
        var repo = new Mock<ILeaderboardRepository>();
        repo.Setup(r => r.GetTopEntriesAsync(region, 50, It.IsAny<PlaceCategory?>())).ReturnsAsync(
        [
            Entry(1, "with-comic", 90, "https://blob.example/comic.png"),
            Entry(2, "no-comic", 80, ""),
            Entry(3, "also-with-comic", 70, "https://blob.example/comic-2.png"),
        ]);

        var blobs = new Mock<IBlobStorageService>();
        blobs.Setup(b => b.BlobExistsAsync(It.IsAny<string>())).ReturnsAsync(true);

        var result = await CreateService(repo, blobs).GetTopComicsAsync(region, 10);

        Assert.Equal(2, result.Count);
        Assert.Equal(1, result[0].Rank);
        Assert.Equal("with-comic", result[0].PlaceId.Value);
        Assert.Equal(2, result[1].Rank);
        Assert.Equal("also-with-comic", result[1].PlaceId.Value);
    }

    [Fact]
    public async Task GetTopComicsAsync_OmitsEntriesWhoseBlobNoLongerExists()
    {
        var region = RegionCode.From("US");
        var repo = new Mock<ILeaderboardRepository>();
        repo.Setup(r => r.GetTopEntriesAsync(region, 50, It.IsAny<PlaceCategory?>())).ReturnsAsync(
        [
            Entry(1, "gone", 95, "https://blob.example/comics/gone.png"),
            Entry(2, "kept", 60, "https://blob.example/comics/kept.png"),
        ]);
        repo.Setup(r => r.UpsertAsync(It.IsAny<LeaderboardEntry>())).Returns(Task.CompletedTask);

        var blobs = new Mock<IBlobStorageService>();
        blobs.Setup(b => b.BlobExistsAsync("https://blob.example/comics/gone.png")).ReturnsAsync(false);
        blobs.Setup(b => b.BlobExistsAsync("https://blob.example/comics/kept.png")).ReturnsAsync(true);

        var result = await CreateService(repo, blobs).GetTopComicsAsync(region, 10);

        Assert.Single(result);
        Assert.Equal(1, result[0].Rank);
        Assert.Equal("kept", result[0].PlaceId.Value);
    }

    [Fact]
    public async Task GetTopComicsAsync_OmitsEntriesWhoseComicIsNoLongerLive()
    {
        var region = RegionCode.From("US");
        var repo = new Mock<ILeaderboardRepository>();
        repo.Setup(r => r.GetTopEntriesAsync(region, 50, It.IsAny<PlaceCategory?>())).ReturnsAsync(
        [
            Entry(1, "expired", 95, "https://blob.example/a.png"),
            Entry(2, "live", 60, "https://blob.example/b.png"),
        ]);

        var blobs = new Mock<IBlobStorageService>();
        blobs.Setup(b => b.BlobExistsAsync(It.IsAny<string>())).ReturnsAsync(true);
        var live = new Mock<ILiveComicLookup>();
        live.Setup(l => l.IsLiveAsync(It.IsAny<PlaceId>())).ReturnsAsync((PlaceId id) => id.Value == "live");

        var result = await CreateService(repo, blobs, live.Object).GetTopComicsAsync(region, 10);

        Assert.Equal("live", Assert.Single(result).PlaceId.Value);
        Assert.Equal(1, result[0].Rank);
    }

    [Fact]
    public async Task GetTopComicsAsync_CapsVisibleComicsAtRequestedLimit()
    {
        var region = RegionCode.From("US");
        var seeded = Enumerable.Range(0, 15)
            .Select(i => Entry(i + 1, $"place-{i}", 100 - i, $"https://blob.example/c{i}.png"))
            .ToList();

        var repo = new Mock<ILeaderboardRepository>();
        repo.Setup(r => r.GetTopEntriesAsync(region, 50, It.IsAny<PlaceCategory?>())).ReturnsAsync(seeded);

        var blobs = new Mock<IBlobStorageService>();
        blobs.Setup(b => b.BlobExistsAsync(It.IsAny<string>())).ReturnsAsync(true);

        var result = await CreateService(repo, blobs).GetTopComicsAsync(region, 10);

        Assert.Equal(10, result.Count);
        Assert.Equal(1, result[0].Rank);
        Assert.Equal(10, result[^1].Rank);
    }

    [Fact]
    public async Task UpsertEntryAsync_LowerRedrawReplacesTheLiveRow()
    {
        var repo = new Mock<ILeaderboardRepository>();
        var saved = default(LeaderboardEntry);
        repo.Setup(r => r.UpsertAsync(It.IsAny<LeaderboardEntry>()))
            .Callback<LeaderboardEntry>(e => saved = e).Returns(Task.CompletedTask);

        await CreateService(repo, new Mock<IBlobStorageService>())
            .UpsertEntryAsync(Entry(0, "p", 30, "https://blob.example/new.png"));

        Assert.Equal(30, saved!.StrangenessScore);
        Assert.Equal("https://blob.example/new.png", saved.ComicBlobUrl);
    }

    [Fact]
    public async Task UpsertEntryAsync_RedrawBelowThresholdRemovesTheLiveRow()
    {
        var repo = new Mock<ILeaderboardRepository>();

        await CreateService(repo, new Mock<IBlobStorageService>())
            .UpsertEntryAsync(Entry(0, "p", 18, "https://blob.example/new.png"));

        repo.Verify(r => r.DeleteAsync(PlaceId.From("p"), RegionCode.From("US")), Times.Once);
        repo.Verify(r => r.UpsertAsync(It.IsAny<LeaderboardEntry>()), Times.Never);
    }

    private static LeaderboardService CreateService(
        Mock<ILeaderboardRepository> repo,
        Mock<IBlobStorageService> blobs,
        ILiveComicLookup? liveComics = null) =>
        new(
            repo.Object,
            blobs.Object,
            NullLogger<LeaderboardService>.Instance,
            Options.Create(new LeaderboardOptions()),
            TimeProvider.System,
            liveComics: liveComics);

    private static LeaderboardEntry Entry(int rank, string placeId, double score, string blobUrl) => new()
    {
        Rank = rank,
        PlaceId = PlaceId.From(placeId),
        RestaurantName = placeId,
        Address = "1 Test St",
        Region = RegionCode.From("US"),
        StrangenessScore = score,
        ComicBlobUrl = blobUrl,
        LastUpdated = DateTimeOffset.UtcNow
    };
}
