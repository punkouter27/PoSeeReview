using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using PoSeeReview.Api.Features.Comics;
using PoSeeReview.Api.Identity;
using PoSeeReview.Api.Storage;
using PoSeeReview.Integration.TestFixtures;
using PoSeeReview.Shared.Ids;
using Xunit;

namespace PoSeeReview.Integration.Services;

/// <summary>
/// The daily spend guard, exercised against real Table Storage.
/// <para>
/// Integration rather than Unit because the whole mechanism <em>is</em> the storage interaction:
/// the limit is enforced by an ETag-guarded increment, and a mocked table would assert only that
/// the code calls methods, not that two counters and a refund actually agree afterwards.
/// </para>
/// <para>
/// This is the only code in the app that stands between a traffic spike and an unbounded bill,
/// which is why it gets the Integration slots.
/// </para>
/// </summary>
[Trait("Tier", "Integration")]
[Trait("Domain", "Comics")]
[Trait("Suite", "CriticalPath")]
public sealed class GenerationBudgetServiceTests : IClassFixture<AzuriteFixture>
{
    private readonly AzuriteFixture _fixture;

    public GenerationBudgetServiceTests(AzuriteFixture fixture) => _fixture = fixture;

    /// <summary>
    /// Builds a service over a table unique to the calling test, so the tests share one Azurite
    /// container without sharing counters.
    /// </summary>
    private async Task<GenerationBudgetService> CreateServiceAsync(
        string tableName,
        string userId,
        int perUserLimit = 3,
        int serviceLimit = 100)
    {
        await _fixture.CreateTestTableAsync(tableName);

        var storageOptions = Options.Create(new AzureStorageOptions { BudgetTableName = tableName });
        var budgetOptions = Options.Create(new GenerationBudgetOptions
        {
            DailyPerUserLimit = perUserLimit,
            DailyServiceLimit = serviceLimit,
            Enabled = true
        });

        var identity = new Mock<ICurrentRequestIdentityAccessor>();
        identity.Setup(i => i.GetCurrentUserId()).Returns(UserId.From(userId));

        var httpContextAccessor = new Mock<IHttpContextAccessor>();
        httpContextAccessor.Setup(a => a.HttpContext).Returns(new DefaultHttpContext());

        return new GenerationBudgetService(
            _fixture.TableServiceClient,
            storageOptions,
            budgetOptions,
            identity.Object,
            httpContextAccessor.Object,
            TimeProvider.System,
            NullLogger<GenerationBudgetService>.Instance);
    }

    [DockerFact]
    public async Task TryReserve_BeyondPerUserLimit_RefusesAndStopsCounting()
    {
        var service = await CreateServiceAsync("BudgetUserLimitTest", "user-over-limit", perUserLimit: 3);

        for (var i = 0; i < 3; i++)
        {
            var allowed = await service.TryReserveAsync();
            Assert.Equal(BudgetDecision.Allowed, allowed.Decision);
        }

        var refused = await service.TryReserveAsync();

        Assert.Equal(BudgetDecision.UserExhausted, refused.Decision);
        Assert.False(refused.IsAllowed);
        Assert.Equal(0, refused.Budget.Remaining);
        Assert.False(refused.Budget.CanGenerate);

        // The refusal must not itself consume budget, or a client retrying would drive the
        // counter arbitrarily far past the limit and corrupt the day's spend record.
        var budget = await service.GetBudgetAsync();
        Assert.Equal(3, budget.Used);
    }

    [DockerFact]
    public async Task Release_AfterCacheHit_ReturnsTheUnitToTheUser()
    {
        var service = await CreateServiceAsync("BudgetRefundTest", "user-refund", perUserLimit: 3);

        await service.TryReserveAsync();
        Assert.Equal(1, (await service.GetBudgetAsync()).Used);

        // What the endpoints do when the pipeline turned out to serve a cached comic: the
        // reservation happened, but no paid work did, so it must come back.
        await service.ReleaseAsync();

        var budget = await service.GetBudgetAsync();
        Assert.Equal(0, budget.Used);
        Assert.Equal(3, budget.Remaining);
    }

    [DockerFact]
    public async Task TryReserve_WhenServiceCeilingReached_RefusesWithoutSpendingUserQuota()
    {
        // A service ceiling below the per-user limit, so the app-wide cap is what bites first.
        var service = await CreateServiceAsync(
            "BudgetServiceLimitTest", "user-service-cap", perUserLimit: 10, serviceLimit: 2);

        Assert.Equal(BudgetDecision.Allowed, (await service.TryReserveAsync()).Decision);
        Assert.Equal(BudgetDecision.Allowed, (await service.TryReserveAsync()).Decision);

        var refused = await service.TryReserveAsync();

        Assert.Equal(BudgetDecision.ServiceExhausted, refused.Decision);
        Assert.False(refused.Budget.ServiceHasCapacity);

        // The distinction that matters: when the app is out of capacity the user must not be
        // charged for a request that was never going to run. Two allowed reservations, so two
        // used — not three.
        Assert.Equal(2, (await service.GetBudgetAsync()).Used);
    }
}
