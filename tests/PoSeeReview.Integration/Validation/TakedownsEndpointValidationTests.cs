using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using PoSeeReview.Api.Features.Comics;
using PoSeeReview.Api.Features.Leaderboard;
using PoSeeReview.Api.Features.Restaurants;
using PoSeeReview.Api.Features.Moderation;
using PoSeeReview.Api.Storage;
using PoSeeReview.Shared.Dtos;
using PoSeeReview.Shared.Validation;
using Xunit;
using PoSeeReview.Shared.Contracts;
using PoSeeReview.Shared.Ids;
using PoSeeReview.Shared.Enums;

namespace PoSeeReview.Integration.Validation;

/// <summary>
/// Takedown slice endpoint behaviour, exercised through the real
/// <see cref="TakedownRequestValidator"/>. Lives in the Integration tier because it
/// wires FluentValidation into the request path (directive #4: all FluentValidation
/// tests belong in the Integration tier, not the No-I/O Unit tier).
/// </summary>
[Trait("Tier", "Integration")]
[Trait("Suite", "CriticalPath")]
public sealed class TakedownsEndpointValidationTests
{
    private readonly Mock<IComicRepository> _mockComicRepository = new();
    private readonly Mock<IBlobStorageService> _mockBlobStorageService = new();
    private readonly Mock<ILeaderboardRepository> _mockLeaderboardRepository = new();
    private readonly Mock<IHallOfFameArchive> _mockHallOfFameArchive = new();
    private readonly Mock<IContentModerationGate> _mockModerationGate = new();
    private readonly Mock<ILogger<TakedownRequestDto>> _mockLogger = new();
    private readonly TelemetryClient _telemetryClient = new(new TelemetryConfiguration { DisableTelemetry = true });

    private Task<IResult> SubmitAsync(TakedownRequestDto request) =>
        TakedownsEndpoints.SubmitAsync(
            request,
            new TakedownRequestValidator(),
            _mockComicRepository.Object,
            _mockBlobStorageService.Object,
            _mockLeaderboardRepository.Object,
            _mockHallOfFameArchive.Object,
            _mockModerationGate.Object,
            _mockLogger.Object,
            _telemetryClient,
            CancellationToken.None);

    private static void AssertAccepted(IResult result) =>
        Assert.Equal(StatusCodes.Status202Accepted, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);

    [DockerFact]
    public async Task SubmitAsync_WhenComicExists_DeletesComicBlobAndLeaderboardEntry()
    {
        var request = BuildValidRequest("ChIJ-abc123", "US-WA-Seattle");
        var placeId = PlaceId.From(request.PlaceId);
        var existingComic = new Comic { Id = ComicId.From("comic-id-001"), PlaceId = placeId };

        _mockComicRepository.Setup(r => r.GetByPlaceIdAsync(placeId)).ReturnsAsync(existingComic);
        _mockLeaderboardRepository.Setup(l => l.GetByPlaceIdAsync(placeId)).ReturnsAsync(BoardRow(placeId));

        var result = await SubmitAsync(request);

        AssertAccepted(result);
        _mockModerationGate.Verify(g => g.SuppressAsync(placeId, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockComicRepository.Verify(r => r.DeleteAsync(placeId), Times.Once);
        _mockBlobStorageService.Verify(b => b.DeleteComicImageAsync(existingComic.Id.Value), Times.Once);
        _mockLeaderboardRepository.Verify(l => l.DeleteAsync(placeId, RegionCode.From("US")), Times.Once);
        _mockHallOfFameArchive.Verify(h => h.DeleteAllForPlaceAsync(placeId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [DockerFact]
    public async Task SubmitAsync_WhenTheComicRowHasExpired_StillErasesTheBoardRowAndArchive()
    {
        // The board row outlives the 24-hour comic and is partitioned by country, not by the
        // request's CC-ST-CITY. Erasing it only alongside a live comic, under the request's
        // region, meant a takedown of an expired comic left everything public in place.
        var request = BuildValidRequest("ChIJ-expired", "US-CA-SanFrancisco");
        var placeId = PlaceId.From(request.PlaceId);

        _mockComicRepository.Setup(r => r.GetByPlaceIdAsync(placeId)).ReturnsAsync((Comic?)null);
        _mockLeaderboardRepository.Setup(l => l.GetByPlaceIdAsync(placeId)).ReturnsAsync(BoardRow(placeId));

        var result = await SubmitAsync(request);

        AssertAccepted(result);
        _mockComicRepository.Verify(r => r.DeleteAsync(It.IsAny<PlaceId>()), Times.Never);
        _mockBlobStorageService.Verify(b => b.DeleteComicImageAsync(It.IsAny<string>()), Times.Never);
        _mockLeaderboardRepository.Verify(l => l.DeleteAsync(placeId, RegionCode.From("US")), Times.Once);
        _mockHallOfFameArchive.Verify(h => h.DeleteAllForPlaceAsync(placeId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [DockerFact]
    public async Task SubmitAsync_WhenComicIdIsEmpty_SkipsBlobDeletion()
    {
        var request = BuildValidRequest("ChIJ-partial", "US-NY-NYC");
        var placeId = PlaceId.From(request.PlaceId);
        var existingComic = new Comic { Id = ComicId.Empty, PlaceId = placeId };
        _mockComicRepository.Setup(r => r.GetByPlaceIdAsync(placeId)).ReturnsAsync(existingComic);

        var result = await SubmitAsync(request);

        AssertAccepted(result);
        _mockComicRepository.Verify(r => r.DeleteAsync(placeId), Times.Once);
        _mockBlobStorageService.Verify(b => b.DeleteComicImageAsync(It.IsAny<string>()), Times.Never);
    }

    [DockerFact]
    public async Task SubmitAsync_AlwaysReturns202Accepted()
    {
        var request = BuildValidRequest("ChIJ-any", "US-WA-SEATTLE");
        _mockComicRepository.Setup(r => r.GetByPlaceIdAsync(It.IsAny<PlaceId>()))
            .ReturnsAsync((Comic?)null);

        var result = await SubmitAsync(request);

        AssertAccepted(result);
    }

    private static LeaderboardEntry BoardRow(PlaceId placeId) => new()
    {
        PlaceId = placeId,
        Region = RegionCode.From("US"),
        RestaurantName = "Test Restaurant"
    };

    private static TakedownRequestDto BuildValidRequest(string placeId, string region) => new()
    {
        PlaceId = placeId,
        ContactEmail = "owner@restaurant.com",
        RequesterName = "Restaurant Owner",
        Region = region,
        Reason = "We do not consent to this content appearing on your platform."
    };
}
