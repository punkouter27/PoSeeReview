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

        // The exact signed-in email (the guest sign-in's own address, read back from /auth/me),
        // in the upper-right of the header.
        var me = await (await signedIn.APIRequest.GetAsync($"{fixture.BaseUrl}/auth/me")).JsonAsync();
        var email = me?.GetProperty("email").GetString();
        Assert.Matches(@"^anon\d{6}@guest\.localhost$", email);
        var emailChip = signedIn.Locator(".po-header__actions .po-header__email");
        await Assertions.Expect(emailChip).ToHaveTextAsync(email!);
        var chipBox = await emailChip.BoundingBoxAsync();
        var width = signedIn.ViewportSize!.Width;
        Assert.True(chipBox!.X + chipBox.Width > width * 0.6 && chipBox.Y < 56, $"email chip at ({chipBox.X},{chipBox.Y}) is not upper-right");
        await Assertions.Expect(signedIn.Locator(".po-header__actions a.po-header__settings")).ToHaveAttributeAsync("href", "/settings");

        // Sign out lives on /settings, behind the gear.
        await signedIn.Locator(".po-header__settings").ClickAsync();
        await Assertions.Expect(signedIn.GetByTestId("settings-sign-out")).ToBeVisibleAsync(new() { Timeout = RenderTimeout });

        // Signed out on an ordinary page: Sign in and the gear instead of the email, and no nav
        // (every nav destination needs a sign-in).
        var anonymous = await fixture.NewPageAsync(viewport);
        await anonymous.GotoAsync($"{fixture.BaseUrl}/diag");
        await Assertions.Expect(anonymous.Locator("h1")).ToHaveTextAsync("System status", new() { Timeout = RenderTimeout });
        await Assertions.Expect(anonymous.Locator(".po-header__email")).ToHaveCountAsync(0);
        await Assertions.Expect(anonymous.Locator(".po-header__actions a.po-link-btn.filled.small", new() { HasTextString = "Sign in" })).ToBeVisibleAsync();
        await Assertions.Expect(anonymous.Locator(".po-header__settings")).ToBeVisibleAsync();
        await Assertions.Expect(anonymous.Locator("nav.nav-links, .po-bottom-nav")).ToHaveCountAsync(0);

        // The sign-in page itself: the header carries the title only, and the page shows the app
        // title and one sign-in button (plus the guest button, which exists only in Dev/Test).
        await anonymous.GotoAsync($"{fixture.BaseUrl}/login");
        await anonymous.Locator(".login-container").WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = RenderTimeout });
        await Assertions.Expect(anonymous.Locator(".po-header .po-header__title")).ToBeVisibleAsync();
        await Assertions.Expect(anonymous.Locator(".po-header__actions")).ToHaveCountAsync(0);
        await Assertions.Expect(anonymous.Locator(".login-container h1")).ToHaveTextAsync("PoSeeReview");
        await Assertions.Expect(anonymous.Locator(".login-container p")).ToHaveCountAsync(0);
        await Assertions.Expect(anonymous.Locator(".login-btn-microsoft")).ToBeVisibleAsync(new() { Timeout = RenderTimeout });
        await Assertions.Expect(anonymous.Locator(".login-container button:not(.login-btn-microsoft)")).ToHaveTextAsync(["Continue as guest"]);
    }

    /// <summary>
    /// The version stamp never covers content: fixed bottom-right on a desktop, and on a phone the
    /// last thing in the page flow, below the page's own content.
    /// </summary>
    [E2ETheory]
    [MemberData(nameof(PlaywrightFixture.Viewports), MemberType = typeof(PlaywrightFixture))]
    public async Task VersionStamp_NeverCoversContent(string viewport)
    {
        var page = await fixture.NewPageAsync(viewport);
        await page.GotoAsync($"{fixture.BaseUrl}/diag");
        await page.GetByTestId("diag-about").WaitForAsync(new() { Timeout = RenderTimeout });

        var position = await page.Locator(".po-version").EvaluateAsync<string>("e => getComputedStyle(e).position");
        Assert.Equal(viewport == "mobile" ? "static" : "fixed", position);

        if (viewport == "mobile")
        {
            var mainBottom = await page.Locator("main").EvaluateAsync<double>("e => e.getBoundingClientRect().bottom + scrollY");
            var stampTop = await page.Locator(".po-version").EvaluateAsync<double>("e => e.getBoundingClientRect().top + scrollY");
            Assert.True(stampTop >= mainBottom - 1, $"stamp top {stampTop} is above the end of main ({mainBottom})");
        }
    }

    /// <summary>
    /// Signed out, every page a visitor can reach (the sign-in page, /diag, /settings and a
    /// sign-in-only route that bounces to the sign-in page) shows no header nav and no bottom nav.
    /// </summary>
    [E2ETheory]
    [MemberData(nameof(PlaywrightFixture.Viewports), MemberType = typeof(PlaywrightFixture))]
    public async Task SignedOut_ShowsNoNavigation(string viewport)
    {
        var page = await fixture.NewPageAsync(viewport);
        foreach (var (route, ready) in new[] { ("/login", ".login-container"), ("/diag", "[data-testid=diag-about]"), ("/settings", ".po-header__actions"), ("/leaderboard", ".login-container") })
        {
            await page.GotoAsync($"{fixture.BaseUrl}{route}");
            await page.Locator(ready).First.WaitForAsync(new() { Timeout = RenderTimeout });
            await Assertions.Expect(page.Locator(".po-header__title")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("nav, .nav-links, .po-bottom-nav")).ToHaveCountAsync(0);
        }
    }

    /// <summary>
    /// Radzen's theme loads before any app stylesheet (the first &lt;link&gt; in the head) and sits
    /// in the `vendor` cascade layer, ordered below the app's own layers, so the app's look wins.
    /// </summary>
    [E2EFact]
    public async Task Radzen_Css_Loads_Before_The_App_Css()
    {
        var page = await fixture.NewPageAsync();
        await page.GotoAsync($"{fixture.BaseUrl}/login");
        await page.Locator(".login-container").WaitForAsync(new() { Timeout = RenderTimeout });

        var sheets = await page.EvaluateAsync<string[]>(
            "() => [...document.querySelectorAll('head link[rel=stylesheet]')].map(l => new URL(l.href).pathname)");
        Assert.Equal("/css/radzen.css", sheets[0]);
        Assert.Contains("/css/app.css", sheets);

        // Inside radzen.css: the layer order statement, then the theme imported into `vendor`,
        // and the theme really loaded (it has rules).
        var radzen = await page.EvaluateAsync<string[]>(@"() => {
            const sheet = [...document.styleSheets].find(s => s.href && s.href.endsWith('/css/radzen.css'));
            return [...sheet.cssRules].map(r => r instanceof CSSLayerStatementRule ? 'layers:' + r.nameList.join(',')
                : r instanceof CSSImportRule ? 'import:' + r.layerName + ':' + (r.styleSheet && r.styleSheet.cssRules.length > 0) + ':' + r.href
                : r.constructor.name);
        }");
        Assert.StartsWith("layers:", radzen[0]);
        var layers = radzen[0]["layers:".Length..].Split(',');
        Assert.True(Array.IndexOf(layers, "vendor") < Array.IndexOf(layers, "plain"));
        Assert.True(Array.IndexOf(layers, "vendor") < Array.IndexOf(layers, "page"));
        Assert.True(Array.IndexOf(layers, "vendor") < Array.IndexOf(layers, "shared"));
        Assert.Matches(@"^import:vendor:true:.*Radzen\.Blazor/css/material-base\.css", radzen[1]);

        // A plain-class Radzen button keeps the app look: the sign-in button is the app's pill, not
        // Radzen's material button (no uppercase text transform, the app's rounded radius).
        var button = page.Locator(".login-btn-microsoft");
        Assert.Equal("none", await button.EvaluateAsync<string>("e => getComputedStyle(e).textTransform"));
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
