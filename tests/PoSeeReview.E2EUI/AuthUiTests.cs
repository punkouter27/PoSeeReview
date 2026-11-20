using Microsoft.Playwright;

namespace PoSeeReview.E2EUI;

/// <summary>
/// C# Playwright coverage of the forced-authentication flow (NET_RULES 4.1/4.4).
/// Requires the app running in Development (guest bypass renders only there).
/// Every test runs against both a mobile portrait and a desktop landscape viewport.
/// </summary>
[Collection("e2e-ui")]
[Trait("Tier", "E2EUI")]
public sealed class AuthUiTests(PlaywrightFixture fixture)
{
    private const int RenderTimeout = 15_000;

    [E2ETheory]
    [MemberData(nameof(PlaywrightFixture.Viewports), MemberType = typeof(PlaywrightFixture))]
    public async Task Home_Unauthenticated_RedirectsToLogin(string viewport)
    {
        var page = await fixture.NewPageAsync(viewport);
        await page.GotoAsync($"{fixture.BaseUrl}/");

        await page.Locator(".login-container").WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = RenderTimeout });
        var microsoftButton = page.GetByRole(AriaRole.Button, new() { NameRegex = new("sign in with microsoft", System.Text.RegularExpressions.RegexOptions.IgnoreCase) });
        await Assertions.Expect(microsoftButton).ToBeVisibleAsync(new() { Timeout = RenderTimeout });
    }

    [E2ETheory]
    [MemberData(nameof(PlaywrightFixture.Viewports), MemberType = typeof(PlaywrightFixture))]
    public async Task GuestLogin_InDev_AuthenticatesAndShowsAnonBadge(string viewport)
    {
        var page = await fixture.NewPageAsync(viewport);
        await page.GotoAsync($"{fixture.BaseUrl}/login");
        await page.Locator(".login-container").WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = RenderTimeout });

        await page.GetByRole(AriaRole.Button, new() { NameRegex = new("continue as guest", System.Text.RegularExpressions.RegexOptions.IgnoreCase) }).ClickAsync();

        // Guest cookie signin round-trips through /auth/login/fake and lands on home.
        await Assertions.Expect(page.Locator(".po-header__email")).ToBeVisibleAsync(new() { Timeout = RenderTimeout });
    }

    [E2ETheory]
    [MemberData(nameof(PlaywrightFixture.Viewports), MemberType = typeof(PlaywrightFixture))]
    public async Task Logout_ReturnsToLoginPage(string viewport)
    {
        var page = await fixture.NewPageAsync(viewport);
        await page.GotoAsync($"{fixture.BaseUrl}/login");
        await page.Locator(".login-container").WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = RenderTimeout });
        await page.GetByRole(AriaRole.Button, new() { NameRegex = new("continue as guest", System.Text.RegularExpressions.RegexOptions.IgnoreCase) }).ClickAsync();
        await Assertions.Expect(page.Locator(".po-header__email")).ToBeVisibleAsync(new() { Timeout = RenderTimeout });

        // Sign out lives on /settings, behind the header's gear (Po web standard).
        await page.Locator(".po-header__settings").ClickAsync();
        await page.GetByTestId("settings-sign-out").ClickAsync(new() { Timeout = RenderTimeout });

        await page.Locator(".login-container").WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = RenderTimeout });
    }
}
