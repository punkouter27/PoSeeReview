using Microsoft.Playwright;

namespace PoSeeReview.E2EUI;

/// <summary>
/// Hall of Fame and Diagnostics page coverage, ported from the former TypeScript suite.
/// Diagnostics covers the anonymous <c>/diag</c> page (fleet standard, 2026-11-17).
/// </summary>
[Collection("e2e-ui")]
[Trait("Tier", "E2EUI")]
public sealed class LeaderboardAndDiagnosticsUiTests(PlaywrightFixture fixture)
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

    // ── Leaderboard ─────────────────────────────────────────────────────────

    [E2ETheory]
    [MemberData(nameof(PlaywrightFixture.Viewports), MemberType = typeof(PlaywrightFixture))]
    public async Task Leaderboard_CardsAlwaysShowAComicStrip(string viewport)
    {
        var page = await SignedInAsync(viewport, "/leaderboard");
        await page.Locator(".leaderboard-container").WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = RenderTimeout });
        await page.Locator("#leaderboard-results[aria-busy='false']")
            .WaitForAsync(new() { Timeout = RenderTimeout });

        await Assertions.Expect(page.Locator(".comic-thumbnail-placeholder")).ToHaveCountAsync(0);

        var cards = page.Locator(".leaderboard-card");
        var count = await cards.CountAsync();
        if (count == 0)
        {
            return;
        }

        await Assertions.Expect(page.Locator(".leaderboard-card .comic-thumbnail img")).ToHaveCountAsync(count);
    }

    /// <summary>
    /// The heading, the minimal default and the never-an-error-banner rule on one page load. The
    /// heading renders through PageShell (.page-hero-title / .page-shell-title). The default view
    /// is the board alone: no region, limit or view chips — one "☰ Right now" button opens the views
    /// drawer (archive, map); beside it, only the restaurants / apartments board switch.
    /// </summary>
    [E2ETheory]
    [MemberData(nameof(PlaywrightFixture.Viewports), MemberType = typeof(PlaywrightFixture))]
    public async Task Leaderboard_DefaultsToTheBareBoard_WithItsHeadingAndNoErrorBanner(string viewport)
    {
        var page = await SignedInAsync(viewport, "/leaderboard");
        await page.Locator(".leaderboard-container").WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = RenderTimeout });

        await Assertions.Expect(page.Locator("h1.page-hero-title, h1.page-shell-title"))
            .ToHaveTextAsync("Hall of Fame");
        await Assertions.Expect(page.Locator(".region-chips, .region-chip, .limit-select, label[for='limit'], .leaderboard-container .chip-toggle:not(.board-views-toggle):not([data-testid^='board-'])"))
            .ToHaveCountAsync(0);
        // The restaurant / apartment board switch (two chips) is the one other control.
        await Assertions.Expect(page.Locator(".board-kind [data-testid^='board-']")).ToHaveCountAsync(2);
        await Assertions.Expect(page.Locator(".board-views-toggle")).ToContainTextAsync("Right now");

        await Assertions.Expect(page.Locator(".alert-danger")).ToHaveCountAsync(0);
        await Assertions.Expect(page.Locator("#blazor-error-ui")).ToBeHiddenAsync(new() { Timeout = RenderTimeout });
    }

    /// <summary>
    /// The /diag contract (AGENTS.md): reachable signed out, titled "System status", a status
    /// banner, then API / Data / Services in that order, the version, and no horizontal scroll at
    /// 390px. Console errors fail the suite through the fixture. /diag is the only route.
    /// </summary>
    [E2ETheory]
    [MemberData(nameof(PlaywrightFixture.Viewports), MemberType = typeof(PlaywrightFixture))]
    public async Task Diag_RendersSignedOut_WithEverySection(string viewport)
    {
        var page = await fixture.NewPageAsync(viewport);
        await page.GotoAsync($"{fixture.BaseUrl}/diag");

        await Assertions.Expect(page).ToHaveTitleAsync("System status");
        await Assertions.Expect(page.Locator("h1")).ToHaveTextAsync("System status");
        var banner = page.GetByTestId("diag-banner");
        await Assertions.Expect(banner).ToHaveTextAsync(new System.Text.RegularExpressions.Regex(@"^(Everything is working|Working, with \d+ things? degraded|Not working: .+)$"), new() { Timeout = RenderTimeout });
        await Assertions.Expect(page.Locator("section.po-diag__group h2")).ToHaveTextAsync(["API", "Data", "Services"]);
        await Assertions.Expect(page.Locator("[data-group='data'] .po-diag__card").First).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("diag-about")).ToContainTextAsync("Version v");

        var refresh = page.GetByRole(AriaRole.Button, new() { NameRegex = new("refresh", System.Text.RegularExpressions.RegexOptions.IgnoreCase) });
        await refresh.ClickAsync();
        await Assertions.Expect(refresh).ToBeEnabledAsync(new() { Timeout = RenderTimeout });

        var overflow = await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > window.innerWidth + 1");
        Assert.False(overflow, "/diag scrolls sideways");
    }
}
