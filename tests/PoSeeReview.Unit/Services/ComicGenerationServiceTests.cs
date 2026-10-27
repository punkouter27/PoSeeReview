using System.Threading;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using PoSeeReview.Api.Features.Comics;
using PoSeeReview.Api.Features.Leaderboard;
using PoSeeReview.Api.Features.Restaurants;
using PoSeeReview.Api.Storage;
using Xunit;
using PoSeeReview.Shared.Contracts;
using PoSeeReview.Shared.Ids;
using PoSeeReview.Shared.Enums;

namespace PoSeeReview.Unit.Services;

/// <summary>
/// Unit tests for ComicGenerationService - orchestrates review analysis, narrative generation, and comic creation
/// </summary>
[Trait("Tier", "Unit")]
[Trait("Suite", "CriticalPath")]
public class ComicGenerationServiceTests
{
    private readonly Mock<IRestaurantService> _mockRestaurantService;
    private readonly Mock<IChatCompletionService> _mockOpenAIService;
    private readonly Mock<IImageGenerationService> _mockImageGenerationService;
    private readonly Mock<IComicTextOverlayService> _mockTextOverlayService;
    private readonly Mock<IBlobStorageService> _mockBlobStorageService;
    private readonly Mock<IComicRepository> _mockComicRepository;
    private readonly Mock<ILeaderboardService> _mockLeaderboardService;
    private readonly Mock<IContentSafetyScreener> _mockContentSafetyScreener;
    private readonly Mock<IContentModerationGate> _mockModerationGate;
    private readonly Mock<ILogger<ComicGenerationService>> _mockLogger;
    private readonly TelemetryClient _telemetryClient;

    public ComicGenerationServiceTests()
    {
        _mockRestaurantService = new Mock<IRestaurantService>();
        _mockOpenAIService = new Mock<IChatCompletionService>();
        _mockImageGenerationService = new Mock<IImageGenerationService>();
        _mockTextOverlayService = new Mock<IComicTextOverlayService>();
        _mockBlobStorageService = new Mock<IBlobStorageService>();
        _mockComicRepository = new Mock<IComicRepository>();
        _mockLeaderboardService = new Mock<ILeaderboardService>();
        _mockContentSafetyScreener = new Mock<IContentSafetyScreener>();
        _mockModerationGate = new Mock<IContentModerationGate>();
        _mockLogger = new Mock<ILogger<ComicGenerationService>>();
        _telemetryClient = new TelemetryClient(new TelemetryConfiguration());
    }

    private ComicGenerationService CreateService(ComicOptions? options = null)
    {
        // Default setup: text overlay returns input bytes unchanged (passthrough)
        _mockTextOverlayService.Setup(x => x.AddTextOverlayAsync(
            It.IsAny<byte[]>(),
            It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync((byte[] imageBytes, IReadOnlyList<string> captions, int panelCount, CancellationToken ct) => imageBytes);

        // Default: the content screen allows. Tests that care about the screen override this.
        _mockContentSafetyScreener
            .Setup(x => x.ScreenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ContentScreenResult.Allowed);

        // Default: moderation has nothing on the place, including at the pre-publish re-check.
        _mockModerationGate
            .Setup(x => x.EvaluateAsync(It.IsAny<PlaceId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ModerationVerdict.Clear);

        return new ComicGenerationService(
            _mockRestaurantService.Object,
            _mockOpenAIService.Object,
            _mockImageGenerationService.Object,
            _mockTextOverlayService.Object,
            _mockBlobStorageService.Object,
            _mockComicRepository.Object,
            _mockLeaderboardService.Object,
            _mockContentSafetyScreener.Object,
            _mockModerationGate.Object,
            _mockLogger.Object,
            _telemetryClient,
            // The real gate: it is a ConcurrentDictionary, and standing in a fake for it would
            // remove the only thing the type does.
            new ComicGenerationLock(),
            Options.Create(options ?? new ComicOptions())
        );
    }

    [Fact]
    public async Task GenerateComicAsync_WithValidRestaurant_ShouldReturnCachedComicIfValid()
    {
        // Arrange
        var placeId = "test-place-123";
        var cachedComic = new Comic
        {
            Id = ComicId.New(),
            PlaceId = PlaceId.From(placeId),
            RestaurantName = "Test Restaurant",
            ImageUrl = "https://example.com/comic.png",
            Narrative = "Test narrative",
            StrangenessScore = 85,
            // The cache key includes the prompt version, so a hand-built "valid" cached comic has
            // to carry the version the service is configured with or it is a cache miss by design.
            PromptVersion = new ComicOptions().PromptVersion,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(12) // Still valid
        };

        var service = CreateService();
        _mockComicRepository.Setup(x => x.GetByPlaceIdAsync(PlaceId.From(placeId)))
            .ReturnsAsync(cachedComic);

        // Act
        var result = await service.GenerateComicAsync(PlaceId.From(placeId), forceRegenerate: false);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(PlaceId.From(placeId), result.PlaceId);
        Assert.Equal(cachedComic.ImageUrl, result.ImageUrl);
        Assert.Equal(ComicCacheState.Cached, result.CacheState);
        _mockImageGenerationService.Verify(x => x.GenerateComicImageAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GenerateComicAsync_WithExpiredCache_ShouldRegenerateComic()
    {
        // Arrange
        var placeId = "test-place-123";
        var expiredComic = new Comic
        {
            PlaceId = PlaceId.From(placeId),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1) // Expired
        };

        var restaurant = new Restaurant
        {
            PlaceId = PlaceId.From(placeId),
            Name = "Test Restaurant",
            Reviews = new List<Review>
            {
                new Review { Text = "This place is bizarre! The waiter wore a dinosaur costume.", Rating = 5 },
                new Review { Text = "Strange but good. They serve food in shoes.", Rating = 4 },
                new Review { Text = "Weirdest experience ever. Worth it!", Rating = 5 },
                new Review { Text = "Normal food, weird ambiance. Furniture upside down.", Rating = 3 },
                new Review { Text = "Surreal dining experience. Loved the backwards menu.", Rating = 5 }
            }
        };

        var service = CreateService();
        _mockComicRepository.Setup(x => x.GetByPlaceIdAsync(PlaceId.From(placeId)))
            .ReturnsAsync(expiredComic);
        _mockRestaurantService.Setup(x => x.GetRestaurantByPlaceIdAsync(PlaceId.From(placeId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(restaurant);
        _mockOpenAIService.Setup(x => x.AnalyzeStrangenessAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StrangenessAnalysis(75, 3, "A restaurant where waiters dress as dinosaurs and food is served in shoes."));
        _mockImageGenerationService.Setup(x => x.GenerateComicImageAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new byte[] { 1, 2, 3, 4 });
        _mockBlobStorageService.Setup(x => x.UploadComicImageAsync(It.IsAny<string>(), It.IsAny<byte[]>()))
            .ReturnsAsync("https://blob.storage/comic.png");

        // Act
        var result = await service.GenerateComicAsync(PlaceId.From(placeId), forceRegenerate: false);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(ComicCacheState.Generated, result.CacheState);
        _mockImageGenerationService.Verify(x => x.GenerateComicImageAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockComicRepository.Verify(x => x.UpsertAsync(It.IsAny<Comic>()), Times.Once);
    }

    [Fact]
    public async Task GenerateComicAsync_WithForceRegenerate_ShouldAlwaysGenerateNewComic()
    {
        // Arrange
        var placeId = "test-place-123";
        var cachedComic = new Comic
        {
            PlaceId = PlaceId.From(placeId),
            PromptVersion = new ComicOptions().PromptVersion,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(12) // Still valid
        };

        var restaurant = new Restaurant
        {
            PlaceId = PlaceId.From(placeId),
            Name = "Test Restaurant",
            Reviews = new List<Review>
            {
                new Review { Text = "Strange review 1", Rating = 5 },
                new Review { Text = "Strange review 2", Rating = 4 },
                new Review { Text = "Strange review 3", Rating = 5 },
                new Review { Text = "Strange review 4", Rating = 3 },
                new Review { Text = "Strange review 5", Rating = 5 }
            }
        };

        var service = CreateService();
        _mockComicRepository.Setup(x => x.GetByPlaceIdAsync(PlaceId.From(placeId)))
            .ReturnsAsync(cachedComic);
        _mockRestaurantService.Setup(x => x.GetRestaurantByPlaceIdAsync(PlaceId.From(placeId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(restaurant);
        _mockOpenAIService.Setup(x => x.AnalyzeStrangenessAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StrangenessAnalysis(75, 3, "Test narrative"));
        _mockImageGenerationService.Setup(x => x.GenerateComicImageAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new byte[] { 1, 2, 3, 4 });
        _mockBlobStorageService.Setup(x => x.UploadComicImageAsync(It.IsAny<string>(), It.IsAny<byte[]>()))
            .ReturnsAsync("https://blob.storage/comic.png");

        // Act
        var result = await service.GenerateComicAsync(PlaceId.From(placeId), forceRegenerate: true);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(ComicCacheState.Generated, result.CacheState);
        _mockImageGenerationService.Verify(x => x.GenerateComicImageAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GenerateComicAsync_ForceRegenerate_ReusesAComicDrawnWhileItWaited()
    {
        // The double-tap on Regenerate: by the time the second request holds the per-place lock,
        // the first has stored a comic created after the second one arrived. Paying again would
        // buy the same thing twice.
        var placeId = PlaceId.From("test-place-123");
        var justDrawn = new Comic
        {
            Id = ComicId.New(),
            PlaceId = placeId,
            ImageUrl = "https://example.com/comic.png",
            PromptVersion = new ComicOptions().PromptVersion,
            CreatedAt = DateTimeOffset.UtcNow.AddSeconds(30),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(12)
        };

        var service = CreateService();
        _mockComicRepository.Setup(x => x.GetByPlaceIdAsync(placeId)).ReturnsAsync(justDrawn);

        var result = await service.GenerateComicAsync(placeId, forceRegenerate: true);

        Assert.Equal(ComicCacheState.Cached, result.CacheState);
        _mockOpenAIService.Verify(x => x.AnalyzeStrangenessAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockImageGenerationService.Verify(x => x.GenerateComicImageAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GenerateComicAsync_WhenWithheldDuringTheDraw_PublishesNothing()
    {
        // A takedown that lands during the image call must not be undone by the write after it.
        var placeId = PlaceId.From("test-place-123");
        var service = CreateService();

        _mockComicRepository.Setup(x => x.GetByPlaceIdAsync(placeId)).ReturnsAsync((Comic?)null);
        _mockRestaurantService.Setup(x => x.GetRestaurantByPlaceIdAsync(placeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Restaurant
            {
                PlaceId = placeId,
                Name = "Test Restaurant",
                Region = RegionCode.From("US"),
                Reviews = [new Review { Text = "A goat served the soup.", Rating = 1 }]
            });
        _mockOpenAIService.Setup(x => x.AnalyzeStrangenessAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StrangenessAnalysis(80, 2, "A goat serves soup."));
        _mockImageGenerationService.Setup(x => x.GenerateComicImageAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([1, 2, 3]);
        _mockModerationGate.Setup(x => x.EvaluateAsync(placeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModerationVerdict(IsHidden: true, IsSuppressed: true, Reason: null));

        var thrown = await Assert.ThrowsAsync<ContentWithheldException>(() => service.GenerateComicAsync(placeId));

        Assert.True(thrown.IsSuppressed);
        _mockBlobStorageService.Verify(x => x.UploadComicImageAsync(It.IsAny<string>(), It.IsAny<byte[]>()), Times.Never);
        _mockComicRepository.Verify(x => x.UpsertAsync(It.IsAny<Comic>()), Times.Never);
        _mockLeaderboardService.Verify(x => x.UpsertEntryAsync(It.IsAny<LeaderboardEntry>()), Times.Never);
    }

    [Fact]
    public async Task GenerateComicAsync_WithFewerReviewsThanConfiguredMinimum_ShouldThrowInsufficientReviews()
    {
        // Arrange — the minimum is configuration, so the test must set it rather than lean on
        // the default. This previously asserted that ONE review was insufficient, which stopped
        // being true when MinimumReviewsRequired dropped to 1; the test then sailed past the
        // guard and failed with a NullReferenceException from deeper in the pipeline.
        var placeId = "test-place-123";
        var restaurant = new Restaurant
        {
            PlaceId = PlaceId.From(placeId),
            Name = "Test Restaurant",
            Reviews = new List<Review>
            {
                new Review { Text = "Only one review", Rating = 5 }
            }
        };

        var service = CreateService(new ComicOptions { MinimumReviewsRequired = 2 });
        _mockComicRepository.Setup(x => x.GetByPlaceIdAsync(PlaceId.From(placeId)))
            .ReturnsAsync((Comic?)null);
        _mockRestaurantService.Setup(x => x.GetRestaurantByPlaceIdAsync(PlaceId.From(placeId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(restaurant);

        // Act & Assert — InsufficientReviewsException derives from InvalidOperationException
        await Assert.ThrowsAsync<InsufficientReviewsException>(() =>
            service.GenerateComicAsync(PlaceId.From(placeId), forceRegenerate: false));
    }

    [Fact]
    public async Task GenerateComicAsync_WhenAnalyzerReturnsEmptyNarrative_ShouldThrowInsteadOfNullReferencing()
    {
        // Arrange — an empty narrative is a plausible AI response, not a bug in our code, and
        // must surface as a handled 400 rather than a NullReferenceException the user reads as
        // "Object reference not set to an instance of an object".
        var placeId = "test-place-123";
        var restaurant = new Restaurant
        {
            PlaceId = PlaceId.From(placeId),
            Name = "Test Restaurant",
            Reviews = new List<Review> { new Review { Text = "A review", Rating = 1 } }
        };

        var service = CreateService();
        _mockComicRepository.Setup(x => x.GetByPlaceIdAsync(PlaceId.From(placeId)))
            .ReturnsAsync((Comic?)null);
        _mockRestaurantService.Setup(x => x.GetRestaurantByPlaceIdAsync(PlaceId.From(placeId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(restaurant);
        _mockOpenAIService.Setup(x => x.AnalyzeStrangenessAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StrangenessAnalysis(72, 2, string.Empty));

        // Act & Assert
        await Assert.ThrowsAsync<InsufficientReviewsException>(() =>
            service.GenerateComicAsync(PlaceId.From(placeId), forceRegenerate: false));

        _mockImageGenerationService.Verify(
            x => x.GenerateComicImageAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GenerateComicAsync_WithMinimumReviews_ShouldSucceed()
    {
        // Arrange
        var placeId = "test-place-123";
        var restaurant = new Restaurant
        {
            PlaceId = PlaceId.From(placeId),
            Name = "Test Restaurant",
            Reviews = new List<Review>
            {
                new Review { Text = "Review 1", Rating = 5 },
                new Review { Text = "Review 2", Rating = 4 },
                new Review { Text = "Review 3", Rating = 5 },
                new Review { Text = "Review 4", Rating = 3 },
                new Review { Text = "Review 5", Rating = 5 }
            }
        };

        var service = CreateService();
        _mockComicRepository.Setup(x => x.GetByPlaceIdAsync(PlaceId.From(placeId)))
            .ReturnsAsync((Comic?)null);
        _mockRestaurantService.Setup(x => x.GetRestaurantByPlaceIdAsync(PlaceId.From(placeId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(restaurant);
        _mockOpenAIService.Setup(x => x.AnalyzeStrangenessAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StrangenessAnalysis(50, 2, "Narrative"));
        _mockImageGenerationService.Setup(x => x.GenerateComicImageAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new byte[] { 1, 2, 3, 4 });
        _mockBlobStorageService.Setup(x => x.UploadComicImageAsync(It.IsAny<string>(), It.IsAny<byte[]>()))
            .ReturnsAsync("https://blob.storage/comic.png");

        // Act
        var result = await service.GenerateComicAsync(PlaceId.From(placeId), forceRegenerate: false);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(PlaceId.From(placeId), result.PlaceId);
    }

    /// <summary>
    /// The client storyboards the wait for the artwork from this one report, so the score and
    /// the finished captions must already be on it — and must be the same captions that get
    /// lettered onto the strip, or the storyboard previews text the comic does not contain.
    /// </summary>
    [Fact]
    public async Task GenerateComicAsync_ReportsScoreAndCaptionsBeforeTheImageCall()
    {
        var placeId = PlaceId.From("test-place-storyboard");
        var reviews = Enumerable.Range(1, 5).Select(i => new Review { Text = $"Review {i}", Rating = 3 }).ToList();
        var service = CreateService();
        _mockComicRepository.Setup(x => x.GetByPlaceIdAsync(placeId)).ReturnsAsync((Comic?)null);
        _mockRestaurantService.Setup(x => x.GetRestaurantByPlaceIdAsync(placeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Restaurant { PlaceId = placeId, Name = "Storyboard Diner", Reviews = reviews });
        _mockOpenAIService.Setup(x => x.AnalyzeStrangenessAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StrangenessAnalysis(88, 2, "Narrative", ["Panel one.", "Panel two."]));

        var reports = new List<ComicGenerationProgress>();
        ComicGenerationProgress? atImageCall = null;
        _mockImageGenerationService.Setup(x => x.GenerateComicImageAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback(() => atImageCall = reports.LastOrDefault())
            .ReturnsAsync(new byte[] { 1, 2, 3, 4 });
        IReadOnlyList<string>? lettered = null;
        _mockTextOverlayService.Setup(x => x.AddTextOverlayAsync(It.IsAny<byte[]>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback((byte[] _, IReadOnlyList<string> captions, int _, CancellationToken _) => lettered = captions)
            .ReturnsAsync((byte[] bytes, IReadOnlyList<string> _, int _, CancellationToken _) => bytes);
        _mockBlobStorageService.Setup(x => x.UploadComicImageAsync(It.IsAny<string>(), It.IsAny<byte[]>()))
            .ReturnsAsync("https://blob.storage/comic.png");

        await service.GenerateComicAsync(placeId, progress: new SyncProgress(reports.Add));

        Assert.NotNull(atImageCall);
        Assert.Equal(ComicGenerationPhase.GeneratingArtwork, atImageCall.Phase);
        Assert.Equal(88, atImageCall.Score);
        Assert.Equal(["Panel one.", "Panel two."], atImageCall.Captions);
        Assert.Equal(atImageCall.Captions, lettered);
        Assert.All(reports.Where(r => r.Phase != ComicGenerationPhase.GeneratingArtwork), r => Assert.Null(r.Captions));
    }

    /// <summary>Reports inline, so the assertions see every report in order.</summary>
    private sealed class SyncProgress(Action<ComicGenerationProgress> report) : IProgress<ComicGenerationProgress>
    {
        public void Report(ComicGenerationProgress value) => report(value);
    }

    [Fact]
    public async Task GenerateComicAsync_ShouldSet7DayCacheExpiration()
    {
        // Arrange
        var placeId = "test-place-123";
        var restaurant = new Restaurant
        {
            PlaceId = PlaceId.From(placeId),
            Name = "Test Restaurant",
            Reviews = Enumerable.Range(1, 10).Select(i => new Review
            {
                Text = $"Review {i}",
                Rating = 5
            }).ToList()
        };

        Comic? capturedComic = null;
        var service = CreateService();
        _mockComicRepository.Setup(x => x.GetByPlaceIdAsync(PlaceId.From(placeId)))
            .ReturnsAsync((Comic?)null);
        _mockRestaurantService.Setup(x => x.GetRestaurantByPlaceIdAsync(PlaceId.From(placeId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(restaurant);
        _mockOpenAIService.Setup(x => x.AnalyzeStrangenessAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StrangenessAnalysis(70, 3, "Narrative"));
        _mockImageGenerationService.Setup(x => x.GenerateComicImageAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new byte[] { 1, 2, 3, 4 });
        _mockBlobStorageService.Setup(x => x.UploadComicImageAsync(It.IsAny<string>(), It.IsAny<byte[]>()))
            .ReturnsAsync("https://blob.storage/comic.png");
        _mockComicRepository.Setup(x => x.UpsertAsync(It.IsAny<Comic>()))
            .Callback<Comic>(comic => capturedComic = comic)
            .Returns(Task.CompletedTask);

        // Act
        await service.GenerateComicAsync(PlaceId.From(placeId), forceRegenerate: false);

        // Assert
        Assert.NotNull(capturedComic);
        // Cache duration is 7 days to reduce AI costs - allow tolerance for test execution time (6-8 days)
        Assert.True(capturedComic.ExpiresAt > DateTimeOffset.UtcNow.AddDays(6),
            $"ExpiresAt {capturedComic.ExpiresAt} should be more than 6 days from now");
        Assert.True(capturedComic.ExpiresAt <= DateTimeOffset.UtcNow.AddDays(8),
            $"ExpiresAt {capturedComic.ExpiresAt} should be less than 8 days from now");
    }

    [Fact]
    public async Task GenerateComicAsync_WhenRestaurantNotFound_ShouldThrowKeyNotFoundException()
    {
        // Arrange
        var placeId = "nonexistent-place";
        var service = CreateService();
        _mockComicRepository.Setup(x => x.GetByPlaceIdAsync(PlaceId.From(placeId)))
            .ReturnsAsync((Comic?)null);
        _mockRestaurantService.Setup(x => x.GetRestaurantByPlaceIdAsync(PlaceId.From(placeId), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException("not found"));

        // Act & Assert
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.GenerateComicAsync(PlaceId.From(placeId), forceRegenerate: false));
    }

    [Fact]
    public async Task GenerateComicAsync_ShouldIncludeRestaurantNameInComic()
    {
        // Arrange
        var placeId = "test-place-123";
        var restaurantName = "The Quirky Diner";
        var restaurant = new Restaurant
        {
            PlaceId = PlaceId.From(placeId),
            Name = restaurantName,
            Reviews = Enumerable.Range(1, 5).Select(i => new Review
            {
                Text = $"Strange review {i}",
                Rating = 5
            }).ToList()
        };

        var service = CreateService();
        _mockComicRepository.Setup(x => x.GetByPlaceIdAsync(PlaceId.From(placeId)))
            .ReturnsAsync((Comic?)null);
        _mockRestaurantService.Setup(x => x.GetRestaurantByPlaceIdAsync(PlaceId.From(placeId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(restaurant);
        _mockOpenAIService.Setup(x => x.AnalyzeStrangenessAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StrangenessAnalysis(80, 4, "Narrative"));
        _mockImageGenerationService.Setup(x => x.GenerateComicImageAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new byte[] { 1, 2, 3, 4 });
        _mockBlobStorageService.Setup(x => x.UploadComicImageAsync(It.IsAny<string>(), It.IsAny<byte[]>()))
            .ReturnsAsync("https://blob.storage/comic.png");

        // Act
        var result = await service.GenerateComicAsync(PlaceId.From(placeId), forceRegenerate: false);

        // Assert
        Assert.Equal(restaurantName, result.RestaurantName);
    }

    #region PrioritizeReviewsByRating Tests

    /// <summary>
    /// Every negative review (3 stars and under) goes first, lowest rating then longest text; the
    /// positives only fill what is left of the analysis window, lowest first.
    /// </summary>
    [Fact]
    public void PrioritizeReviewsByRating_PutsNegativesFirst_AndFillsTheWindowWithPositives()
    {
        var reviews = new List<Review>
        {
            new() { Rating = 5, Text = "Excellent!" },
            new() { Rating = 1, Text = "Awful service!" },
            new() { Rating = 3, Text = "Mediocre" },
            new() { Rating = 1, Text = "Terrible place, worst experience ever!" },
            new() { Rating = 4, Text = "Pretty good" },
            new() { Rating = 2, Text = "Not good at all" }
        };

        var method = typeof(ComicGenerationService).GetMethod(
            "PrioritizeReviewsByRating",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var result = (List<Review>)method!.Invoke(CreateService(), [reviews])!;

        Assert.Equal([1, 1, 2, 3, 4], result.Select(r => r.Rating));
        Assert.Equal("Terrible place, worst experience ever!", result[0].Text);
    }

    #endregion

    #region FilterInappropriateReviews Tests

    [Theory]
    [InlineData("This food is fuck terrible")]  // Exact word match
    [InlineData("What a shit restaurant")]      // Exact word match
    [InlineData("The service was ass")]         // Exact word match
    [InlineData("The waiter was a bitch")]      // Exact word match
    public void FilterInappropriateReviews_Masks_ProfanityInsteadOfDroppingTheReview(string inappropriateText)
    {
        // Arrange
        var reviews = new List<string>
        {
            "Great place!",
            inappropriateText,
            "Decent food"
        };

        var service = CreateService();

        // Act
        var method = typeof(ComicGenerationService).GetMethod(
            "FilterInappropriateReviews",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var result = (List<string>)method!.Invoke(service, new object[] { reviews })!;

        // Assert
        Assert.Equal(3, result.Count);
        Assert.Contains("[bleep]", result[1]);
        Assert.DoesNotContain(inappropriateText, result);
    }

    [Fact]
    public void FilterInappropriateReviews_MasksLeetAndAnyCase_AndDropsOnlyExplicitReviews()
    {
        // Arrange
        var reviews = new List<string>
        {
            "Waited 45 min, total SH1T, but the goat in the kitchen was a hell of a show",
            "The waiter described his penis to us",
            "Nice restaurant"
        };

        var service = CreateService();

        // Act
        var method = typeof(ComicGenerationService).GetMethod(
            "FilterInappropriateReviews",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var result = (List<string>)method!.Invoke(service, new object[] { reviews })!;

        // Assert: digits outside the masked word survive, "hell" is not profanity, explicit drops.
        Assert.Equal(
            ["Waited 45 min, total [bleep], but the goat in the kitchen was a hell of a show", "Nice restaurant"],
            result);
    }

    [Fact]
    public void FilterInappropriateReviews_Does_Not_Filter_Partial_Matches()
    {
        // Arrange - Words containing profanity but not exact matches
        var reviews = new List<string>
        {
            "This restaurant is shitty",  // Contains "shit" but as part of "shitty"
            "The fucking service",        // Contains "fuck" but as part of "fucking"
            "Nice place"
        };

        var service = CreateService();

        // Act
        var method = typeof(ComicGenerationService).GetMethod(
            "FilterInappropriateReviews",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var result = (List<string>)method!.Invoke(service, new object[] { reviews })!;

        // Assert - Should keep all reviews since words are not exact matches
        Assert.Equal(3, result.Count);
    }

    #endregion
}
