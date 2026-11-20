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
    [DockerTheory]
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

    [DockerFact]
    public async Task Diag_IsAnonymous_ReportsEnvironment_AndNeverShowsASecret()
    {
        var body = await factory.CreateClient().GetStringAsync("/api/diag");
        var report = JsonSerializer.Deserialize<DiagReportDto>(body, JsonSerializerOptions.Web);

        Assert.NotNull(report);
        Assert.False(string.IsNullOrWhiteSpace(report.Environment));
        Assert.NotEqual(default, report.CheckedAt);
        Assert.NotEmpty(report.Checks);
        Assert.All(report.Checks, c => Assert.Contains(c.Group, new[] { DiagGroups.Api, DiagGroups.Data, DiagGroups.Service }));

        // The factory injects these values; none may appear, masked or not.
        foreach (var leak in new[] { "test-key-12345", "test-google-maps-key", "test.openai.azure.com", "AccountKey" })
        {
            Assert.DoesNotContain(leak, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [DockerFact]
    public async Task DiagMockStatus_ReportsActiveMocks()
    {
        var status = await factory.CreateClient().GetFromJsonAsync<MockStatusDto>("/api/diag/mock-status");

        Assert.NotNull(status);
        // The E2E factory registers fake comic + restaurant services as IMockable.
        Assert.True(status.IsMockActive);
        Assert.NotEmpty(status.ActiveMocks);
    }
}
