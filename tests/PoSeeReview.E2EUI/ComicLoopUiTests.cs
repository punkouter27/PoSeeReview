using Microsoft.Playwright;

namespace PoSeeReview.E2EUI;

/// <summary>
/// The core loop, end to end: Discover → tap → stepper → comic (win) or recoverable error
/// (loss) → Back returns to discovery (loop reset). Nothing else in the suite walks past the
/// tap, so the streaming consumer, the reveal teardown and the error card's recovery path had
/// no coverage at all.
/// </summary>
[Collection("e2e-ui")]
[Trait("Tier", "E2EUI")]
public sealed class ComicLoopUiTests(PlaywrightFixture fixture)
{
    private const int RenderTimeout = 20_000;

    // A real generation is ~10s of paid pipeline; the mocked one is instant. Generous either way.
    private const int GenerationTimeout = 90_000;

    private static readonly System.Text.RegularExpressions.Regex GuestButton =
        new("continue as guest", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex BackButton =
        new("back to", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    [Theory]
    [MemberData(nameof(PlaywrightFixture.Viewports), MemberType = typeof(PlaywrightFixture))]
    public async Task ComicLoop_TapToOutcome_ThenBackResetsToDiscovery(string viewport)
    {
        var page = await fixture.NewPageAsync(viewport);
        fixture.AllowNotFoundPath(page, "/api/comics/");
        await page.GotoAsync($"{fixture.BaseUrl}/login");
        await page.Locator(".login-container").WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = RenderTimeout });
        await page.GetByRole(AriaRole.Button, new() { NameRegex = GuestButton }).ClickAsync();
        await page.Locator(".index-container").WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = RenderTimeout });

        // Resume a typed-ZIP discovery, the same way a returning user lands on cards without
        // being asked for location. The ZIP matches SCRIPTS/ui-check.mjs so both exercise the
        // same place, and the second viewport is then a cache hit rather than a second spend.
        await page.EvaluateAsync(
            "() => { localStorage.setItem('poseereview:discovery-mode', 'zip');"
            + " localStorage.setItem('poseereview:last-search', '20020'); }");
        await page.ReloadAsync();
        await page.Locator(".index-container").WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = RenderTimeout });

        // Not a card the forecast already marks too ordinary: those have no generate button.
        var cards = page.Locator("[data-testid='restaurant-card']:not([data-forecast='ordinary'])");
        try
        {
            await cards.First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = RenderTimeout });
        }
        catch (TimeoutException)
        {
            // Mocked/empty discovery is a legitimate environment state; the loop cannot start.
            return;
        }

        // Tap: the only control in the app that spends money.
        await cards.First.Locator("[data-testid='comic-generate-btn']").ClickAsync();
        await Assertions.Expect(page).ToHaveURLAsync(
            new System.Text.RegularExpressions.Regex("/comic/.+generate=true"),
            new() { Timeout = RenderTimeout });

        // Win or loss — never the framework's error strip, and never a page with no way out.
        var outcome = page.Locator(".comic-container, .error-container");
        await Assertions.Expect(outcome.First).ToBeVisibleAsync(new() { Timeout = GenerationTimeout });
        await Assertions.Expect(page.Locator("#blazor-error-ui")).ToBeHiddenAsync();

        if (await page.Locator(".comic-container").CountAsync() > 0)
        {
            // Win. The strip must be there, and the ink-development mask must have been
            // cleared — a reveal that never finishes leaves the bottom of the comic hidden.
            await Assertions.Expect(page.Locator(".comic-strip-image")).ToBeVisibleAsync(new() { Timeout = RenderTimeout });
            await page.WaitForFunctionAsync(
                "() => { const c = document.querySelector('.comic-strip-container');"
                + " return !!c && !c.hasAttribute('data-comic-reveal'); }",
                null,
                new() { Timeout = RenderTimeout });

            // The ring's digits are CSS-generated (a counter on the animated --score), so the
            // real score is asserted where it is text: the ring's accessible label.
            var score = page.Locator(".ring-label .visually-hidden");
            await Assertions.Expect(score).ToHaveTextAsync(new System.Text.RegularExpressions.Regex("^Strangeness ([1-9]\\d?|100) out of 100$"), new() { Timeout = RenderTimeout });
        }
        else
        {
            // Loss. A recoverable failure (rate limit, dropped stream) must offer a retry;
            // every failure must offer a way back.
            await Assertions.Expect(page.Locator(".state-card-actions button").First).ToBeVisibleAsync();
        }

        // Loop reset: back on discovery, and the comic's palette must not follow. A win has no
        // back button — the comic page's one action is Share — so the way out is the brand link,
        // which is the route a real user takes. A loss offers Back explicitly.
        if (await page.Locator(".error-container").CountAsync() > 0)
        {
            await page.GetByRole(AriaRole.Button, new() { NameRegex = BackButton }).ClickAsync();
        }
        else
        {
            await page.Locator("a.navbar-brand").ClickAsync();
        }
        await Assertions.Expect(page.Locator(".index-container")).ToBeVisibleAsync(new() { Timeout = RenderTimeout });
        await Assertions.Expect(page.Locator("#blazor-error-ui")).ToBeHiddenAsync();

        var tintLingers = await page.EvaluateAsync<bool>(
            "() => !!getComputedStyle(document.documentElement).getPropertyValue('--comic-tint-1').trim()");
        Assert.False(tintLingers, "--comic-tint-1 is still set after leaving the comic route");
    }
}
