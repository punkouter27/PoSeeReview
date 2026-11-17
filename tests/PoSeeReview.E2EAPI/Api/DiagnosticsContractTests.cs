using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PoSeeReview.Api;
using PoSeeReview.Shared.Dtos;
using Xunit;

namespace PoSeeReview.E2EAPI.Api;

/// <summary>
/// Contract tests for the ops surface (NET_RULES 3.2): <c>/health</c> (+ live/ready) and
/// <c>/api/diag</c>. Both are anonymous so platform probes and the signed-out <c>/diag</c> page
/// can reach them, which makes "no secrets in /api/diag" a security boundary rather than a nicety.
/// </summary>
public class DiagnosticsContractTests(CustomWebApplicationFactory<Program> factory)
    : IClassFixture<CustomWebApplicationFactory<Program>>
{
    [Theory]
    [InlineData("/health")]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task HealthEndpoints_AreAnonymous_AndRespond(string path)
    {
        var response = await factory.CreateClient().GetAsync(path);

        // Deny-by-default must not apply: an unauthenticated probe never sees 401/403.
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        // The deploy reads this header to confirm the new build is the one answering.
        Assert.True(response.Headers.Contains("X-Commit"));
    }

    [Fact]
    public async Task Diag_IsAnonymous_ReportsEnvironment_AndNeverShowsASecret()
    {
        var body = await factory.CreateClient().GetStringAsync("/api/diag");
        var snapshot = JsonSerializer.Deserialize<DiagnosticsSnapshotDto>(body, JsonSerializerOptions.Web);

        Assert.NotNull(snapshot);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.Environment));
        Assert.NotEqual(default, snapshot.Timestamp);
        Assert.NotEmpty(snapshot.Checks);

        // The factory injects these values; none may appear, masked or not.
        foreach (var leak in new[] { "test-key-12345", "test-google-maps-key", "test.openai.azure.com", "AccountKey" })
        {
            Assert.DoesNotContain(leak, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task DiagMockStatus_ReportsActiveMocks()
    {
        var status = await factory.CreateClient().GetFromJsonAsync<MockStatusDto>("/api/diag/mock-status");

        Assert.NotNull(status);
        // The E2E factory registers fake comic + restaurant services as IMockable.
        Assert.True(status.IsMockActive);
        Assert.NotEmpty(status.ActiveMocks);
    }
}
