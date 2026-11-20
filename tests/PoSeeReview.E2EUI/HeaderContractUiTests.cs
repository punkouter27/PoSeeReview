using Microsoft.Playwright;

namespace PoSeeReview.E2EUI;

/// <summary>
/// Enforces the Po shell header contract (Po web standard, 2026-11): the app name upper-left
/// (<c>.po-header__title</c>), the signed-in email (or the Sign in button) and the settings gear
/// upper-right, on every page signed in AND out, plus one <c>v{YYMMDD}</c> build stamp. The right
/// zone regressed once by being wrapped in a Development-only conditional, so it is asserted
/// explicitly in both states.
/// </summary>
[Collection("e2e-ui")]
[Trait("Tier", "E2EUI")]
public sealed class HeaderContractUiTests(PlaywrightFixture fixture)
{
    private const int RenderTimeout = 15_000;

    private async Task<IPage> SignedInPageAsync(string viewport)
    {
        var page = await fixture.NewPageAsync(viewport);
        await page.GotoAsync($"{fixture.BaseUrl}/login");
        await page.Locator(".login-container").WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = RenderTimeout });
        await page.GetByRole(AriaRole.Button, new() { NameRegex = new("continue as guest", System.Text.RegularExpressions.RegexOptions.IgnoreCase) }).ClickAsync();
        await Assertions.Expect(page.Locator(".po-header__email")).ToBeVisibleAsync(new() { Timeout = RenderTimeout });
        return page;
    }

    [E2ETheory]
    [MemberData(nameof(PlaywrightFixture.Viewports), MemberType = typeof(PlaywrightFixture))]
    public async Task Header_LeftZone_ShowsBranding(string viewport)
    {
        var page = await SignedInPageAsync(viewport);

        // The name stays readable at phone width: no collapse to the mark.
        await Assertions.Expect(page.Locator(".po-header__title")).ToHaveTextAsync("PoSeeReview");
        await Assertions.Expect(page.Locator(".po-header__title .brand-name")).ToBeVisibleAsync();

        // One build stamp, vYYMMDD, on every page.
        await Assertions.Expect(page.Locator(".po-version")).ToHaveCountAsync(1);
        await Assertions.Expect(page.Locator(".po-version")).ToHaveTextAsync(new System.Text.RegularExpressions.Regex(@"^v\d{6}$"));
    }

    [E2ETheory]
    [MemberData(nameof(PlaywrightFixture.Viewports), MemberType = typeof(PlaywrightFixture))]
    public async Task Header_CenterZone_ShowsPrimaryNavigation(string viewport)
    {
        var page = await SignedInPageAsync(viewport);

        var nav = page.Locator("nav.nav-links");
        await Assertions.Expect(nav.Locator(".nav-item")).ToHaveCountAsync(1);
        if (viewport == "mobile")
        {
            // On a phone the bottom nav carries Hall of Fame, so the bar keeps room for the name.
            await Assertions.Expect(nav).ToBeHiddenAsync();
            await Assertions.Expect(page.Locator(".po-bottom-nav")).ToBeVisibleAsync();
        }
        else
        {
            await Assertions.Expect(nav).ToBeVisibleAsync(new() { Timeout = RenderTimeout });
        }
    }

    /// <summary>
    /// Both halves of the right-zone contract: the email and the gear with a principal; the Sign in
    /// button and the gear without one. One test because the two states are a single rule, and
    /// this tier is method-counted.
    /// </summary>
    [E2ETheory]
    [MemberData(nameof(PlaywrightFixture.Viewports), MemberType = typeof(PlaywrightFixture))]
    public async Task Header_RightZone_ShowsSessionOnlyWhenAuthenticated(string viewport)
    {
        var signedIn = await SignedInPageAsync(viewport);

        await Assertions.Expect(signedIn.Locator(".po-header__actions .po-header__email")).ToContainTextAsync("@");
        await Assertions.Expect(signedIn.Locator(".po-header__actions a.po-header__settings")).ToHaveAttributeAsync("href", "/settings");

        // Sign out lives on /settings, behind the gear.
        await signedIn.Locator(".po-header__settings").ClickAsync();
        await Assertions.Expect(signedIn.GetByTestId("settings-sign-out")).ToBeVisibleAsync(new() { Timeout = RenderTimeout });

        // Signed out, the header is still there: Sign in and the gear instead of the email, with
        // the sign-in card in the page body under it.
        var anonymous = await fixture.NewPageAsync(viewport);
        await anonymous.GotoAsync($"{fixture.BaseUrl}/login");
        await anonymous.Locator(".login-container").WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = RenderTimeout });

        await Assertions.Expect(anonymous.Locator(".po-header")).ToBeVisibleAsync();
        await Assertions.Expect(anonymous.Locator(".po-header__email")).ToHaveCountAsync(0);
        await Assertions.Expect(anonymous.Locator(".po-header__actions a.po-link-btn.filled.small", new() { HasTextString = "Sign in" })).ToBeVisibleAsync();
        await Assertions.Expect(anonymous.Locator(".po-header__settings")).ToBeVisibleAsync();
        await Assertions.Expect(anonymous.Locator(".login-btn-microsoft")).ToBeVisibleAsync(new() { Timeout = RenderTimeout });
    }

    [E2ETheory]
    [MemberData(nameof(PlaywrightFixture.Viewports), MemberType = typeof(PlaywrightFixture))]
    public async Task Header_MockDataBanner_ReflectsServerMockStatus(string viewport)
    {
        var page = await SignedInPageAsync(viewport);

        // /api/diag/mock-status is the single source of truth (NET_RULES 4.2); the badge must
        // agree with it rather than guessing from the environment name.
        var status = await page.APIRequest.GetAsync($"{fixture.BaseUrl}/api/diag/mock-status");
        var json = await status.JsonAsync();
        var isMockActive = json?.GetProperty("isMockActive").GetBoolean() ?? false;

        var badge = page.Locator(".nav-mock-badge");
        if (!isMockActive)
        {
            await Assertions.Expect(badge).ToHaveCountAsync(0);
        }
        else if (viewport == "mobile")
        {
            // Hidden on a phone so the name fits; /diag still says so.
            await Assertions.Expect(badge).ToBeHiddenAsync();
        }
        else
        {
            await Assertions.Expect(badge).ToBeVisibleAsync(new() { Timeout = RenderTimeout });
            await Assertions.Expect(badge).ToHaveTextAsync("USING MOCK DATA");
        }
    }
}
