using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PoSeeReview.Api;
using Xunit;

namespace PoSeeReview.E2EAPI.Api;

/// <summary>
/// Contract tests for the deny-by-default authorization policy (NET_RULES 4.1/4.5): every
/// endpoint requires a session unless it explicitly opts out with AllowAnonymous.
/// </summary>
public class AuthorizationContractTests(CustomWebApplicationFactory<Program> factory)
    : IClassFixture<CustomWebApplicationFactory<Program>>
{
    // Every route a signed-out caller may reach. A new public route fails this test until it is
    // added here on purpose. Static files and the Blazor host are public by design and are
    // identified by their metadata, not listed.
    private static readonly string[] AnonymousAllowList =
    [
        "/api/takedowns/",
        "/api/{**segments}",
        "/auth/login/fake",
        "/auth/login/microsoft",
        "/auth/logout",
        "/auth/me",
        "/diag/",
        "/diag/mock-status",
        "/duel/{**rest}",
        "/health",
        "/health/live",
        "/health/ready",
        "/openapi/{documentName}.json",
        "/s/{code}",
        "/scalar/favicon.svg",
        "/scalar/scalar.aspnetcore.js",
        "/scalar/scalar.js",
        "/scalar/{documentName?}",
        "/share/{placeId}/card.png",
        "/{*path:nonfile}",
    ];

    [Fact]
    public void Anonymous_surface_is_exactly_the_allow_list()
    {
        var anonymous = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Where(e => !IsHostOrStaticAsset(e))
            .Select(e => "/" + e.RoutePattern.RawText!.TrimStart('/'))
            .Distinct()
            .Order(StringComparer.Ordinal);

        Assert.Equal(AnonymousAllowList, anonymous);
    }

    [Fact]
    public void Every_other_endpoint_requires_a_signed_in_user()
    {
        var policy = factory.Services.GetRequiredService<IOptions<AuthorizationOptions>>().Value.FallbackPolicy;

        Assert.NotNull(policy);
        Assert.Contains(policy.Requirements, r => r is DenyAnonymousAuthorizationRequirement);
    }

    private static bool IsHostOrStaticAsset(RouteEndpoint endpoint) =>
        endpoint.RoutePattern.RawText!.TrimStart('/').StartsWith("_framework", StringComparison.Ordinal)
        || endpoint.Metadata.Any(m => m.GetType().Name is "StaticAssetDescriptor" or "ComponentTypeMetadata" or "RootComponentMetadata");

    [Theory]
    [InlineData("/api/comics/ChIJTest123")]
    [InlineData("/api/restaurants/nearby?latitude=47.6&longitude=-122.3")]
    [InlineData("/api/leaderboard")]
    public async Task BusinessEndpoints_WithoutSession_Are401(string path)
    {
        var response = await factory.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/auth/me")]
    [InlineData("/api/devsession")]
    [InlineData("/diag")]
    [InlineData("/health")]
    public async Task AnonymousEndpoints_WithoutSession_AreNot401(string path)
    {
        var response = await factory.CreateClient().GetAsync(path);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SpaFallback_IsReachableAnonymously()
    {
        // Regression guard: the fallback must stay a real endpoint carrying AllowAnonymous,
        // otherwise the deny-by-default policy 401s "/" and the login flow is unreachable.
        var response = await factory.CreateClient().GetAsync("/some/client/route");

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UnknownApiRoute_Returns404_RatherThanSpaShell()
    {
        var response = await factory.CreateClient().GetAsync("/api/definitely-not-a-route");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task FakeAuthHeader_GrantsAccessToDeniedEndpoint()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Fake-User", "authz-contract-user");

        var response = await client.GetAsync("/api/leaderboard");

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
