using System.Net;
using PoSeeReview.Api;
using Xunit;

namespace PoSeeReview.E2EAPI.Api;

/// <summary>
/// Fast in-memory API contract tests that intentionally avoid real Azure and Google dependencies.
/// </summary>
public class ApiContractTests : IClassFixture<CustomWebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public ApiContractTests(CustomWebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
        // Business endpoints deny by default (NET_RULES 4.1/4.5); authenticate via the
        // Test-only FakeAuth scheme so these contract calls exercise the real handlers.
        _client.DefaultRequestHeaders.Add("X-Fake-User", "contract-test-user");
    }

    [DockerFact]
    public async Task StreamComic_WithInvalidPlaceId_EndsWith404Event()
    {
        // The stream commits a 200 before generation can fail, so the real status is in the frame.
        var response = await _client.PostAsync("/api/comics/invalid-place-id-123/stream", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"errorStatus\":404", body);
    }

    [DockerFact]
    public async Task GetComic_WithNonExistentComic_Returns404()
    {
        var response = await _client.GetAsync("/api/comics/ChIJNonExistentPlace123456789");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // Every response carries the baseline security headers, errors included.
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("strict-origin-when-cross-origin", response.Headers.GetValues("Referrer-Policy").Single());
    }

}
