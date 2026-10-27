using Microsoft.Playwright;

namespace PoSeeReview.E2EUI;

/// <summary>
/// Hall of Fame and Diagnostics page coverage, ported from the former TypeScript suite.
/// Diagnostics doubles as the UI-side check that <c>/diag</c> renders masked values only
/// (NET_RULES 3.2).
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
        await page.Locator(".nav-user-zone").WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = RenderTimeout });
        await page.GotoAsync($"{fixture.BaseUrl}{route}");
        return page;
    }

    // ── Leaderboard ─────────────────────────────────────────────────────────

    [Theory]
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
    /// is the board alone: no region, limit or view chips — the archive and map are links under it.
    /// </summary>
    [Theory]
    [MemberData(nameof(PlaywrightFixture.Viewports), MemberType = typeof(PlaywrightFixture))]
    public async Task Leaderboard_DefaultsToTheBareBoard_WithItsHeadingAndNoErrorBanner(string viewport)
    {
        var page = await SignedInAsync(viewport, "/leaderboard");
        await page.Locator(".leaderboard-container").WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = RenderTimeout });

        await Assertions.Expect(page.Locator("h1.page-hero-title, h1.page-shell-title"))
            .ToHaveTextAsync("Hall of Fame");
        await Assertions.Expect(page.Locator(".region-chips, .region-chip, .limit-select, label[for='limit'], .leaderboard-container .chip-toggle"))
            .ToHaveCountAsync(0);

        await Assertions.Expect(page.Locator(".alert-danger")).ToHaveCountAsync(0);
        await Assertions.Expect(page.Locator("#blazor-error-ui")).ToBeHiddenAsync(new() { Timeout = RenderTimeout });
    }

    [Theory]
    [MemberData(nameof(PlaywrightFixture.Viewports), MemberType = typeof(PlaywrightFixture))]
    public async Task Diagnostics_NeverRendersAnUnmaskedSecret(string viewport)
    {
        var page = await SignedInAsync(viewport, "/diagnostics");
        await page.Locator(".diagnostics-container").First
            .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = RenderTimeout });

        // Dependency health, folded in from the former Diagnostics_ShowsDependencyHealth:
        // same route, same render, one page load instead of two.
        await Assertions.Expect(page.Locator(".health-summary, .diagnostics-section").First)
            .ToBeVisibleAsync(new() { Timeout = RenderTimeout });

        var rows = page.Locator(".config-row");
        var count = await rows.CountAsync();
        for (var i = 0; i < count; i++)
        {
            var key = (await rows.Nth(i).Locator(".config-key").TextContentAsync() ?? string.Empty).ToLowerInvariant();
            if (!key.Contains("key") && !key.Contains("secret") && !key.Contains("password") && !key.Contains("connectionstring"))
            {
                continue;
            }

            var value = (await rows.Nth(i).Locator(".config-value").TextContentAsync() ?? string.Empty).Trim();
            if (value.Length == 0 || value.Equals("(not set)", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Assert.Contains("***", value, StringComparison.Ordinal);
        }
    }
}
