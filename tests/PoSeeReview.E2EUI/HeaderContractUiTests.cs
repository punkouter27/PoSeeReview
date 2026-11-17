using Microsoft.Playwright;

namespace PoSeeReview.E2EUI;

/// <summary>
/// Enforces the header layout contract (NET_RULES 4.1): Left (branding) | Center (actions) |
/// Right (session / logout). The right zone regressed once by being wrapped in a
/// Development-only conditional, so it is asserted explicitly whenever a principal exists.
/// Anonymous visitors get no session zone at all — /login is already the sign-in CTA.
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
        await Assertions.Expect(page.Locator(".nav-user-zone")).ToBeVisibleAsync(new() { Timeout = RenderTimeout });
        return page;
    }

    [Theory]
    [MemberData(nameof(PlaywrightFixture.Viewports), MemberType = typeof(PlaywrightFixture))]
    public async Task Header_LeftZone_ShowsBranding(string viewport)
    {
        var page = await SignedInPageAsync(viewport);

        await Assertions.Expect(page.Locator(".navbar-brand .brand-name")).ToHaveTextAsync("PoSeeReview");
    }

    [Theory]
    [MemberData(nameof(PlaywrightFixture.Viewports), MemberType = typeof(PlaywrightFixture))]
    public async Task Header_CenterZone_ShowsPrimaryNavigation(string viewport)
    {
        var page = await SignedInPageAsync(viewport);

        var nav = page.Locator("nav.nav-links");
        await Assertions.Expect(nav).ToBeVisibleAsync(new() { Timeout = RenderTimeout });
        await Assertions.Expect(nav.Locator(".nav-item")).ToHaveCountAsync(1);
    }

    /// <summary>
    /// Both halves of the right-zone contract: present with a principal, absent without one.
    /// One test because the two states are a single rule, and this tier is method-counted.
    /// </summary>
    [Theory]
    [MemberData(nameof(PlaywrightFixture.Viewports), MemberType = typeof(PlaywrightFixture))]
    public async Task Header_RightZone_ShowsSessionOnlyWhenAuthenticated(string viewport)
    {
        var signedIn = await SignedInPageAsync(viewport);

        await Assertions.Expect(signedIn.Locator(".nav-user-zone .nav-user-badge")).ToBeVisibleAsync(new() { Timeout = RenderTimeout });

        // Sign out lives in the account menu the badge opens.
        await signedIn.Locator(".nav-user-zone .nav-user-badge").ClickAsync();
        await Assertions.Expect(signedIn.GetByRole(AriaRole.Button, new()
        {
            NameRegex = new("sign out", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
        })).ToBeVisibleAsync(new() { Timeout = RenderTimeout });

        // An anonymous visitor is already on /login, which presents the sign-in choice as its
        // primary CTA. The header deliberately carries no "Not signed in / Sign in" pair — it
        // restated the page and competed with that CTA.
        var anonymous = await fixture.NewPageAsync(viewport);
        await anonymous.GotoAsync($"{fixture.BaseUrl}/login");
        await anonymous.Locator(".login-container").WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = RenderTimeout });

        await Assertions.Expect(anonymous.Locator(".nav-user-zone")).ToHaveCountAsync(0);
        await Assertions.Expect(anonymous.Locator(".login-btn-microsoft")).ToBeVisibleAsync(new() { Timeout = RenderTimeout });
    }

    [Theory]
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
        if (isMockActive)
        {
            await Assertions.Expect(badge).ToBeVisibleAsync(new() { Timeout = RenderTimeout });
            await Assertions.Expect(badge).ToHaveTextAsync("USING MOCK DATA");
        }
        else
        {
            await Assertions.Expect(badge).ToHaveCountAsync(0);
        }
    }

}
