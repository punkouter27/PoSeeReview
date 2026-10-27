using Microsoft.Playwright;

namespace PoSeeReview.E2EUI;

/// <summary>
/// Discovery and comic flows, ported from the former standalone TypeScript Playwright suite
/// so the repo keeps a single E2E UI project (NET_RULES 2.2). Geolocation is granted through
/// the browser context rather than stubbed in JS, so the real interop path is exercised.
/// </summary>
[Collection("e2e-ui")]
[Trait("Tier", "E2EUI")]
public sealed class DiscoveryUiTests(PlaywrightFixture fixture)
{
    private const int RenderTimeout = 20_000;
    private static readonly System.Text.RegularExpressions.Regex GuestButton =
        new("continue as guest", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private async Task<IPage> HomeAsync(string viewport)
    {
        var page = await fixture.NewPageAsync(viewport);
        await page.GotoAsync($"{fixture.BaseUrl}/login");
        await page.Locator(".login-container").WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = RenderTimeout });
        await page.GetByRole(AriaRole.Button, new() { NameRegex = GuestButton }).ClickAsync();
        await page.Locator(".index-container").WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = RenderTimeout });
        return page;
    }

    [Theory]
    [MemberData(nameof(PlaywrightFixture.Viewports), MemberType = typeof(PlaywrightFixture))]
    public async Task Home_RendersHeroAndLocationPrompt_WithNoErrorBanner(string viewport)
    {
        var page = await HomeAsync(viewport);

        await Assertions.Expect(page.Locator(".app-header h1")).ToHaveTextAsync("Turn a restaurant's wildest reviews into a comic");
        await Assertions.Expect(page.Locator(".location-prompt")).ToBeVisibleAsync(new() { Timeout = RenderTimeout });

        // The framework's error strip stays display:none unless something threw.
        await Assertions.Expect(page.Locator("#blazor-error-ui")).ToBeHiddenAsync(new() { Timeout = RenderTimeout });
        await Assertions.Expect(page.Locator(".error-message")).ToHaveCountAsync(0);
    }

    [Theory]
    [MemberData(nameof(PlaywrightFixture.Viewports), MemberType = typeof(PlaywrightFixture))]
    public async Task Home_GrantedGeolocation_TriggersRestaurantLookup(string viewport)
    {
        var page = await fixture.NewPageAsync(viewport);
        await page.Context.GrantPermissionsAsync(["geolocation"]);
        await page.Context.SetGeolocationAsync(new() { Latitude = 47.6062f, Longitude = -122.3321f });

        await page.GotoAsync($"{fixture.BaseUrl}/login");
        await page.Locator(".login-container").WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = RenderTimeout });
        await page.GetByRole(AriaRole.Button, new() { NameRegex = GuestButton }).ClickAsync();
        await page.Locator(".index-container").WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = RenderTimeout });

        var nearbyRequest = page.WaitForRequestAsync(r => r.Url.Contains("/api/restaurants/nearby"), new() { Timeout = RenderTimeout });
        await page.Locator(".location-btn").ClickAsync();

        var request = await nearbyRequest;
        // The JS interop must forward the granted coordinates to the API, not placeholders.
        Assert.Contains("latitude=47.6", request.Url, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("longitude=-122.3", request.Url, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(PlaywrightFixture.Viewports), MemberType = typeof(PlaywrightFixture))]
    public async Task Home_DeniedGeolocation_ShowsRecoverableMessage(string viewport)
    {
        var page = await fixture.NewPageAsync(viewport);
        await page.Context.ClearPermissionsAsync();

        await page.GotoAsync($"{fixture.BaseUrl}/login");
        await page.Locator(".login-container").WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = RenderTimeout });
        await page.GetByRole(AriaRole.Button, new() { NameRegex = GuestButton }).ClickAsync();
        await page.Locator(".index-container").WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = RenderTimeout });

        await page.Locator(".location-btn").ClickAsync();

        // Either the denial alert appears or the manual search stays available — the page
        // must never dead-end when the browser refuses location.
        var recovered = page.Locator(".denied-alert, .search-row");
        await Assertions.Expect(recovered.First).ToBeVisibleAsync(new() { Timeout = RenderTimeout });
    }

    [Theory]
    [MemberData(nameof(PlaywrightFixture.Viewports), MemberType = typeof(PlaywrightFixture))]
    public async Task Home_WithRememberedZip_ResumesTextSearchWithoutAskingForLocation(string viewport)
    {
        var page = await HomeAsync(viewport);

        // A returning user whose last successful discovery was a typed ZIP. The page must
        // re-run that search and leave geolocation alone — the older posee_location_enabled
        // flag could only say "auto-request GPS", so a ZIP user was re-prompted every visit.
        await page.EvaluateAsync(
            "() => { localStorage.setItem('posee_discovery_mode', 'zip');"
            + " localStorage.setItem('posee_last_search', '10001'); }");

        var nearbyCalls = new List<string>();
        page.Request += (_, request) =>
        {
            if (request.Url.Contains("/api/restaurants/nearby", StringComparison.OrdinalIgnoreCase))
            {
                nearbyCalls.Add(request.Url);
            }
        };

        var searchRequest = page.WaitForRequestAsync(
            r => r.Url.Contains("/api/restaurants/search", StringComparison.OrdinalIgnoreCase),
            new() { Timeout = RenderTimeout });

        await page.ReloadAsync();

        var resumed = await searchRequest;
        Assert.Contains("location=10001", resumed.Url, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(nearbyCalls);
    }

    [Theory]
    [MemberData(nameof(PlaywrightFixture.Viewports), MemberType = typeof(PlaywrightFixture))]
    public async Task RestaurantCards_WhenPresent_ExposeGenerateAction(string viewport)
    {
        var page = await HomeAsync(viewport);

        // Not a card the forecast already marks too ordinary: those have no generate button.
        var cards = page.Locator("[data-testid='restaurant-card']:not([data-forecast='ordinary'])");
        if (await cards.CountAsync() == 0)
        {
            // Mocked/empty discovery is a legitimate environment state; nothing to assert.
            return;
        }

        await Assertions.Expect(cards.First.Locator("[data-testid='comic-generate-btn']"))
            .ToBeVisibleAsync(new() { Timeout = RenderTimeout });
    }

    [Theory]
    [MemberData(nameof(PlaywrightFixture.Viewports), MemberType = typeof(PlaywrightFixture))]
    public async Task ComicRoute_WithUnknownPlace_RendersWithoutCrashing(string viewport)
    {
        var page = await HomeAsync(viewport);

        await page.GotoAsync($"{fixture.BaseUrl}/comic/ChIJDefinitelyNotARealPlace");

        // Not-found is a legitimate outcome; an unhandled exception strip is not.
        await Assertions.Expect(page.Locator("#blazor-error-ui")).ToBeHiddenAsync(new() { Timeout = RenderTimeout });
    }
}
