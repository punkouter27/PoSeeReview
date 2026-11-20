using Microsoft.Playwright;

namespace PoSeeReview.E2EUI;

/// <summary><c>/moderation</c>: operator tooling, reachable by URL and gated on a role.</summary>
[Collection("e2e-ui")]
[Trait("Tier", "E2EUI")]
public sealed class ModerationUiTests(PlaywrightFixture fixture)
{
    private const int RenderTimeout = 20_000;

    private static readonly System.Text.RegularExpressions.Regex GuestButton =
        new("continue as guest", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private async Task<IPage> SignedInAsync(string viewport, string route)
    {
        var page = await fixture.NewPageAsync(viewport);
        await page.GotoAsync($"{fixture.BaseUrl}/login");
        await page.Locator(".login-container").WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = RenderTimeout });
        await page.GetByRole(AriaRole.Button, new() { NameRegex = GuestButton }).ClickAsync();
        await page.Locator(".po-header__email").WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = RenderTimeout });
        await page.GotoAsync($"{fixture.BaseUrl}{route}");
        return page;
    }

    /// <summary>
    /// The refusal rather than an empty queue, plus the absence of a primary-nav entry. Same
    /// route and same render as the former pair: operator tooling is reachable by URL and stays
    /// out of a consumer app's navigation.
    /// </summary>
    [E2ETheory]
    [MemberData(nameof(PlaywrightFixture.Viewports), MemberType = typeof(PlaywrightFixture))]
    public async Task Moderation_ShowsTheRefusalAndHasNoPrimaryNavEntry(string viewport)
    {
        var page = await SignedInAsync(viewport, "/moderation");

        // A guest session holds no roles. The client check is UI-only — every endpoint enforces
        // the same policy server-side — but it should still be the thing on screen rather than
        // an empty queue that looks like there is nothing to moderate.
        await Assertions.Expect(page.Locator(".state-card-title")).ToHaveTextAsync("Moderators only");
        await Assertions.Expect(page.Locator(".moderation-list")).ToHaveCountAsync(0);

        await Assertions.Expect(page.Locator("nav.nav-links a[href*='moderation']")).ToHaveCountAsync(0);
    }
}
