using System.Net.Http.Json;
using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using PoSeeReview.Api;
using PoSeeReview.Shared.Dtos;
using Xunit;

namespace PoSeeReview.E2EAPI.Api;

/// <summary>
/// Contract tests for the BFF auth slice (NET_RULES 4.4): /auth/me state, FakeAuth
/// header mapping, guest cookie login, and open-redirect sanitization.
/// </summary>
public class AuthEndpointsTests(CustomWebApplicationFactory<Program> factory)
    : IClassFixture<CustomWebApplicationFactory<Program>>
{
    private HttpClient CreateHttpsClient() => factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false
    });

    [Fact]
    public async Task AuthMe_IsAnonymousWithoutHeaders_AndMapsFakeHeadersToClaims()
    {
        var client = CreateHttpsClient();
        var anonymous = await client.GetFromJsonAsync<AuthStateDto>("/auth/me");

        Assert.NotNull(anonymous);
        Assert.False(anonymous.IsAuthenticated);
        Assert.Null(anonymous.UserId);

        var request = new HttpRequestMessage(HttpMethod.Get, "/auth/me");
        request.Headers.Add("X-Fake-User", "test-user-42");
        request.Headers.Add("X-Fake-Roles", "Admin, Tester");

        var response = await client.SendAsync(request);
        var state = await response.Content.ReadFromJsonAsync<AuthStateDto>();

        Assert.NotNull(state);
        Assert.True(state.IsAuthenticated);
        Assert.Equal("test-user-42", state.UserId);
        Assert.Contains("Admin", state.Roles);
        Assert.Contains("Tester", state.Roles);
    }

    [Fact]
    public async Task FakeLogin_SetsBffCookie_AndRedirectsToLocalReturnUrl()
    {
        var response = await CreateHttpsClient().GetAsync("/auth/login/fake?returnUrl=/leaderboard");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/leaderboard", response.Headers.Location?.ToString());
        var setCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("__Host-PoSeeReview.Auth", StringComparison.Ordinal));
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A non-local returnUrl collapses to the root (open-redirect guard); logout lands on /login.</summary>
    [Theory]
    [InlineData("/auth/login/fake?returnUrl=https%3A%2F%2Fevil.example%2Fphish", "/")]
    [InlineData("/auth/login/fake?returnUrl=%2F%2Fevil.example", "/")]
    [InlineData("/auth/login/fake?returnUrl=%2F%5Cevil.example", "/")]
    [InlineData("/auth/logout", "/login")]
    public async Task AuthRedirects_StayLocal(string path, string expectedLocation)
    {
        var response = await CreateHttpsClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(expectedLocation, response.Headers.Location?.ToString());
    }
}
