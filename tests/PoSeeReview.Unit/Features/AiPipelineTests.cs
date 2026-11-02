using PoSeeReview.Api.Features.Comics.Painters;
using System.Text.Json;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using PoSeeReview.Api.Features.Comics;
using PoSeeReview.Api.Features.Moderation;
using PoSeeReview.Api.HostedServices;
using PoSeeReview.Api.Storage;
using PoSeeReview.Api.Telemetry;
using PoSeeReview.Shared.Contracts;
using PoSeeReview.Shared.Enums;
using PoSeeReview.Shared.Ids;
using Xunit;

namespace PoSeeReview.Unit.Features;

/// <summary>
/// The AI pipeline's money and trust rules: what a call costs, that the prompt stays cacheable,
/// that citations are checked, and that the background painters spend only where they should.
/// </summary>
[Trait("Tier", "Unit")]
public sealed class AiPipelineTests
{
    [Fact]
    public void ToAnalysis_KeepsTheRatings_AndDropsCitationsOutsideTheReviewsSent()
    {
        var analysis = ChatPrompts.ToAnalysis(new StrangenessAnalysisResult
        {
            Absurdity = 7,
            Specificity = 12,   // clamped
            StoryPotential = 6,
            Narrative = "A goat runs the till.",
            Panels =
            [
                new PanelResult { Scene = "A goat at the till.", Caption = "A goat rings up lunch.", ReviewIds = [2, 2, 9, 0] },
                new PanelResult { Scene = "Change in hay.", Caption = "Change arrives as hay.", ReviewIds = [] }
            ]
        }, reviewCount: 3);

        Assert.Equal(new StrangenessRatings(7, 10, 6), analysis.Ratings);
        Assert.Equal([2], analysis.PanelSources![0]);   // 9 and 0 were never shown; 2 de-duplicated
        Assert.Empty(analysis.PanelSources[1]);
        Assert.Equal(1, ChatPrompts.CountUngroundedPanels(analysis));
    }

    [Fact]
    public void AnalysisSystemMessage_StaysAboveThePromptCacheThreshold()
    {
        // Azure caches a prefix of 1024+ tokens; at ~4.3 characters a token for this English, a
        // trim below this length silently turns the discount off. Measured: 1024 of ~1265 cached.
        Assert.True(ChatPrompts.AnalysisSystemMessage.Length >= 4400,
            $"System message is {ChatPrompts.AnalysisSystemMessage.Length} chars; keep it above the 1024-token cache threshold.");
        Assert.Contains("reviewIds", ChatPrompts.AnalysisSystemMessage);
    }

    [Fact]
    public void Pricing_BillsCachedInputAtTheCachedRate_AndAppsettingsRatesArePer1K()
    {
        var nano = new AiModelRate { InputPer1KTokens = 0.0002, CachedInputPer1KTokens = 0.00002, OutputPer1KTokens = 0.00125 };
        Assert.Equal(0.000279, AiCostTracker.PriceOf(nano, inputTokens: 1265, outputTokens: 168, cachedInputTokens: 1024), 6);
        Assert.Equal(0.0002, AiCostTracker.PriceOf(new AiModelRate { InputPer1KTokens = 0.0002 }, 1000, 0, 1000), 6);

        // The unit trap that over-reported every scorer call ~300x: a per-million price typed
        // into a per-1K field. No configured text rate is anywhere near $50 per million tokens.
        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "PoSeeReview.Api", "appsettings.json")));
        foreach (var model in settings.RootElement.GetProperty("AiPricing").GetProperty("Models").EnumerateObject())
        {
            Assert.True(model.Value.GetProperty("InputPer1KTokens").GetDouble() < 0.05, $"{model.Name} input rate looks per-million");
        }
    }

    [Theory]
    [InlineData(0, 0, 0, 0, null)]
    [InlineData(0, 4, 0, 0, "Flag:cs-hate")]     // only the top severity refuses; 4 publishes and alerts
    [InlineData(0, 6, 0, 0, "Block:cs-hate")]
    [InlineData(2, 0, 0, 0, null)]            // low severity is not worth a push
    [InlineData(0, 0, 2, 0, null)]          // cartoon slapstick: mild violence is the genre
    [InlineData(0, 0, 4, 0, "Flag:cs-violence")]
    [InlineData(0, 0, 0, 6, "Block:cs-selfharm")]
    public void ContentSafetyGrade_MapsSeverityOntoTheScreensGrades(int sexual, int hate, int violence, int selfHarm, string? expected)
    {
        var result = AzureContentSafetyScreener.Grade(new Dictionary<string, int>
        {
            ["Sexual"] = sexual, ["Hate"] = hate, ["Violence"] = violence, ["SelfHarm"] = selfHarm
        });

        Assert.Equal(expected, result is null ? null : $"{result.Outcome}:{result.Category}");
    }

    [Fact]
    public void ComicEntity_RoundTripsBreakdownSourcesAndRemasterState_AndOlderRowsReadAsNone()
    {
        var comic = new Comic
        {
            PlaceId = PlaceId.From("place-1"),
            Breakdown = new ComicScoreBreakdown(8, 7, 9),
            Sources = [new ComicSource(1, 2, 1), new ComicSource(2, 4, 2)],
            ImageModel = "gpt-image-1-mini",
            RemasterState = RemasterState.Declined
        };

        var back = ComicEntity.FromDomain(comic).ToDomain();
        Assert.Equal(comic.Breakdown, back.Breakdown);
        Assert.Equal(comic.Sources, back.Sources);
        Assert.Equal("gpt-image-1-mini", back.ImageModel);
        Assert.Equal(RemasterState.Declined, back.RemasterState);

        var older = new ComicEntity { PlaceId = "place-1" }.ToDomain();
        Assert.Null(older.Breakdown);
        Assert.Empty(older.Sources);
        Assert.Equal(RemasterState.None, older.RemasterState);
    }

    [Fact]
    public async Task Generation_WithACachedAnalysisForTheSameReviews_KeepsTheScoreAndSkipsTheScorer()
    {
        var harness = new PipelineHarness();
        var cached = new StrangenessAnalysis(66, 1, "A goat runs the till.", ["A goat rings up lunch."], ["A goat at the till."],
            new StrangenessRatings(7, 8, 9), [[1]]);
        harness.Analyses
            .Setup(a => a.GetAsync(harness.PlaceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new CachedAnalysis(harness.PlaceId, "Joe's", harness.CapturedHash ?? "", new ComicOptions().PromptVersion, cached, [new ComicSource(1, 1, 1)], 0));

        // First pass learns the hash the pipeline computes, second pass must hit it.
        harness.Analyses
            .Setup(a => a.SaveAsync(It.IsAny<CachedAnalysis>(), It.IsAny<CancellationToken>()))
            .Callback<CachedAnalysis, CancellationToken>((saved, _) => harness.CapturedHash = saved.ReviewsHash)
            .Returns(Task.CompletedTask);
        await harness.Service.GenerateComicAsync(harness.PlaceId, forceRegenerate: true);
        harness.Chat.Invocations.Clear();

        var comic = await harness.Service.GenerateComicAsync(harness.PlaceId, forceRegenerate: true);

        harness.Chat.Verify(c => c.AnalyzeStrangenessAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(66, comic.StrangenessScore);
        Assert.Equal(new ComicScoreBreakdown(7, 8, 9), comic.Breakdown);
    }

    [Fact]
    public async Task Generation_OnACacheMiss_StoresTheVerdictWithEachCitedReviewsStars()
    {
        var harness = new PipelineHarness();
        CachedAnalysis? saved = null;
        harness.Analyses
            .Setup(a => a.SaveAsync(It.IsAny<CachedAnalysis>(), It.IsAny<CancellationToken>()))
            .Callback<CachedAnalysis, CancellationToken>((value, _) => saved = value)
            .Returns(Task.CompletedTask);

        var comic = await harness.Service.GenerateComicAsync(harness.PlaceId);

        // Reviews go to the scorer lowest-rated first: review 1 is the 1-star, review 2 the 4-star.
        Assert.NotNull(saved);
        Assert.Equal([new ComicSource(1, 1, 1), new ComicSource(2, 2, 4)], saved!.Sources);
        Assert.Equal(saved.Sources, comic.Sources);
        Assert.Equal("test-painter", comic.ImageModel);
    }

    [Fact]
    public async Task Remaster_RedrawsALiveDraftInPlace_AndLeavesComicsWithNoRecordedModel()
    {
        var now = DateTimeOffset.UtcNow;
        Comic Live(string id, string model) => new()
        {
            Id = ComicId.From($"comic-{id}"),
            PlaceId = PlaceId.From(id),
            ImageUrl = $"https://blob.example/comics/comic-{id}.webp?sig=old",
            Scenes = ["A goat at the till."],
            Captions = ["A goat rings up lunch."],
            ImageModel = model,
            PromptVersion = new ComicOptions().PromptVersion,
            ExpiresAt = now.AddDays(3)
        };

        var comics = new Dictionary<string, Comic> { ["draft"] = Live("draft", "gpt-image-1-mini"), ["older"] = Live("older", "") };
        var repo = new Mock<IComicRepository>();
        repo.Setup(r => r.GetByPlaceIdAsync(It.IsAny<PlaceId>())).ReturnsAsync((PlaceId p) => comics[p.Value]);
        var board = new Mock<ILeaderboardRepository>();
        board.Setup(b => b.GetTopEntriesGlobalAsync(It.IsAny<int>())).ReturnsAsync(
        [
            new LeaderboardEntry { PlaceId = PlaceId.From("older") },
            new LeaderboardEntry { PlaceId = PlaceId.From("draft") }
        ]);
        var painter = new Mock<IImageGenerationService>();
        painter.SetupGet(p => p.Model).Returns("gemini-2.5-flash-image");
        painter.Setup(p => p.GenerateComicImageAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync([1, 2, 3]);
        var overlay = new Mock<IComicTextOverlayService>();
        overlay.Setup(o => o.AddTextOverlayAsync(It.IsAny<byte[]>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((byte[] b, IReadOnlyList<string> _, int _, CancellationToken _) => b);
        var blobs = new Mock<IBlobStorageService>();
        blobs.Setup(b => b.UploadComicImageAsync("comic-draft", It.IsAny<byte[]>())).ReturnsAsync("https://blob.example/comics/comic-draft.webp?sig=new");
        var gate = new Mock<IContentModerationGate>();
        gate.Setup(g => g.EvaluateAsync(It.IsAny<PlaceId>(), It.IsAny<CancellationToken>())).ReturnsAsync(ModerationVerdict.Clear);

        var services = new ServiceCollection()
            .AddScoped(_ => repo.Object).AddScoped(_ => board.Object).AddScoped(_ => overlay.Object)
            .AddScoped(_ => blobs.Object).AddScoped(_ => gate.Object)
            .AddKeyedScoped(RemasterOptions.ServiceKey, (_, _) => painter.Object)
            .BuildServiceProvider();

        var remaster = new ComicRemasterService(
            services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new RemasterOptions()), Options.Create(new ComicOptions()),
            new ComicGenerationLock(), new TelemetryClient(new TelemetryConfiguration()), TimeProvider.System,
            NullLogger<ComicRemasterService>.Instance);

        Assert.Equal(1, await remaster.RunOnceAsync(CancellationToken.None));
        Assert.Equal(RemasterState.Remastered, comics["draft"].RemasterState);
        Assert.Equal("gemini-2.5-flash-image", comics["draft"].ImageModel);
        Assert.EndsWith("sig=new", comics["draft"].ImageUrl);
        Assert.Equal(RemasterState.None, comics["older"].RemasterState);
        painter.Verify(p => p.GenerateComicImageAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Predraw_ZeroesDemandBeforeEachAttempt_AndSkipsPlacesThatAreAlreadyDrawn()
    {
        var analysis = new StrangenessAnalysis(70, 1, "n", ["c"], ["s"]);
        CachedAnalysis Candidate(string id) => new(PlaceId.From(id), id, "h", new ComicOptions().PromptVersion, analysis, [], 5);

        var analyses = new Mock<IAnalysisRepository>();
        analyses.Setup(a => a.GetPredrawCandidatesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Candidate("drawn"), Candidate("wanted")]);
        var comics = new Mock<IComicRepository>();
        comics.Setup(c => c.GetByPlaceIdAsync(PlaceId.From("drawn"))).ReturnsAsync(new Comic
        {
            PromptVersion = new ComicOptions().PromptVersion,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
        });
        var gate = new Mock<IContentModerationGate>();
        gate.Setup(g => g.EvaluateAsync(It.IsAny<PlaceId>(), It.IsAny<CancellationToken>())).ReturnsAsync(ModerationVerdict.Clear);
        var generator = new Mock<IComicGenerationService>();
        generator.Setup(g => g.GenerateComicAsync(PlaceId.From("wanted"), false, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ImageDeclinedException("no"));

        var services = new ServiceCollection()
            .AddScoped(_ => analyses.Object).AddScoped(_ => comics.Object).AddScoped(_ => gate.Object).AddScoped(_ => generator.Object)
            .BuildServiceProvider();

        var predraw = new ComicPredrawService(
            services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new PredrawOptions()), Options.Create(new ComicOptions()),
            new TelemetryClient(new TelemetryConfiguration()), TimeProvider.System, NullLogger<ComicPredrawService>.Instance);

        Assert.Equal(0, await predraw.RunOnceAsync(CancellationToken.None));
        // A refusal must not come back tomorrow and bill the image again.
        analyses.Verify(a => a.ResetDemandAsync(PlaceId.From("wanted"), It.IsAny<CancellationToken>()), Times.Once);
        analyses.Verify(a => a.ResetDemandAsync(PlaceId.From("drawn"), It.IsAny<CancellationToken>()), Times.Never);
        generator.Verify(g => g.GenerateComicAsync(PlaceId.From("drawn"), It.IsAny<bool>(), It.IsAny<IProgress<ComicGenerationProgress>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>A generation pipeline over mocks: one place, two reviews, a scorer that cites both.</summary>
    private sealed class PipelineHarness
    {
        public PlaceId PlaceId { get; } = PlaceId.From("place-ai");
        public Mock<IChatCompletionService> Chat { get; } = new();
        public Mock<IAnalysisRepository> Analyses { get; } = new();
        public string? CapturedHash { get; set; }
        public ComicGenerationService Service { get; }

        public PipelineHarness()
        {
            var restaurants = new Mock<IRestaurantService>();
            restaurants.Setup(r => r.GetRestaurantByPlaceIdAsync(PlaceId, It.IsAny<CancellationToken>())).ReturnsAsync(new Restaurant
            {
                PlaceId = PlaceId,
                Name = "Joe's",
                Reviews =
                [
                    new Review { Text = "Lovely place, a goat runs the till.", Rating = 4 },
                    new Review { Text = "The goat gave me change in hay.", Rating = 1 }
                ]
            });

            Chat.Setup(c => c.AnalyzeStrangenessAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(
                new StrangenessAnalysis(70, 2, "A goat runs the till.", ["A goat rings up.", "Change in hay."], ["Goat at till.", "Hay as change."],
                    new StrangenessRatings(7, 8, 9), [[1], [2]]));

            var painter = new Mock<IImageGenerationService>();
            painter.SetupGet(p => p.Model).Returns("test-painter");
            painter.Setup(p => p.GenerateComicImageAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync([1]);
            var overlay = new Mock<IComicTextOverlayService>();
            overlay.Setup(o => o.AddTextOverlayAsync(It.IsAny<byte[]>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([1]);
            var blobs = new Mock<IBlobStorageService>();
            blobs.Setup(b => b.UploadComicImageAsync(It.IsAny<string>(), It.IsAny<byte[]>())).ReturnsAsync("https://blob.example/c.webp");
            var screener = new Mock<IContentSafetyScreener>();
            screener.Setup(s => s.ScreenAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(ContentScreenResult.Allowed);
            var gate = new Mock<IContentModerationGate>();
            gate.Setup(g => g.EvaluateAsync(It.IsAny<PlaceId>(), It.IsAny<CancellationToken>())).ReturnsAsync(ModerationVerdict.Clear);

            Service = new ComicGenerationService(
                restaurants.Object, Chat.Object, painter.Object, overlay.Object, blobs.Object,
                new Mock<IComicRepository>().Object, new Mock<ILeaderboardService>().Object, screener.Object, gate.Object,
                NullLogger<ComicGenerationService>.Instance, new TelemetryClient(new TelemetryConfiguration()),
                new ComicGenerationLock(), Options.Create(new ComicOptions()), Analyses.Object);
        }
    }
}
