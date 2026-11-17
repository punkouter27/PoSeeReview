using Microsoft.Playwright;

namespace PoSeeReview.E2EUI;

/// <summary>
/// Verifies system-aware light/dark theming (NET_RULES 4.3). The page must follow the OS
/// preference by default, and an explicit <c>data-theme</c> choice must beat it in both
/// directions. Card surfaces are asserted too, because those were the tokens most likely
/// to stay stuck on a hardcoded white.
/// </summary>
[Collection("e2e-ui")]
[Trait("Tier", "E2EUI")]
public sealed class ThemeUiTests(PlaywrightFixture fixture)
{
    private const int RenderTimeout = 15_000;

    private const string LightSurface = "rgb(248, 247, 255)"; // #F8F7FF
    private const string DarkSurface = "rgb(18, 16, 26)";     // #12101A

    /// <summary>
    /// The colour a token resolves to, not its declared text: the tokens are written as
    /// <c>light-dark(light, dark)</c>, which only a used value (a probe's background) resolves.
    /// </summary>
    private static async Task<string> TokenAsync(IPage page, string token) =>
        (await page.EvaluateAsync<string>(
            $"() => {{ const p = document.createElement('div'); p.style.background = 'var({token})';"
            + " document.body.appendChild(p); const c = getComputedStyle(p).backgroundColor; p.remove(); return c; }")) ?? string.Empty;

    private async Task<IPage> LoginPageAsync(string viewport, ColorScheme scheme)
    {
        var page = await fixture.NewPageAsync(viewport);
        await page.EmulateMediaAsync(new() { ColorScheme = scheme });
        await page.GotoAsync($"{fixture.BaseUrl}/login");
        await page.Locator(".login-container").WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = RenderTimeout });
        return page;
    }

    /// <summary>
    /// Both halves of "the OS preference wins by default". Kept as one test because the two
    /// schemes are one contract, and because this tier's budget is method-counted — the
    /// assertions and the viewport coverage are unchanged.
    /// </summary>
    [Theory]
    [MemberData(nameof(PlaywrightFixture.Viewports), MemberType = typeof(PlaywrightFixture))]
    public async Task Theme_FollowsOsPreference(string viewport)
    {
        var light = await LoginPageAsync(viewport, ColorScheme.Light);
        Assert.Equal(LightSurface, await TokenAsync(light, "--color-surface"));

        var dark = await LoginPageAsync(viewport, ColorScheme.Dark);
        Assert.Equal(DarkSurface, await TokenAsync(dark, "--color-surface"));
    }

    /// <summary>
    /// An explicit <c>data-theme</c> beats the OS in both directions — the case that matters
    /// because only <c>ThemeUiTests</c> ever sets that attribute.
    /// </summary>
    [Theory]
    [MemberData(nameof(PlaywrightFixture.Viewports), MemberType = typeof(PlaywrightFixture))]
    public async Task Theme_ExplicitChoiceOverridesTheOs(string viewport)
    {
        var light = await LoginPageAsync(viewport, ColorScheme.Light);
        await light.EvaluateAsync("() => document.documentElement.setAttribute('data-theme', 'dark')");
        Assert.Equal(DarkSurface, await TokenAsync(light, "--color-surface"));

        var dark = await LoginPageAsync(viewport, ColorScheme.Dark);
        await dark.EvaluateAsync("() => document.documentElement.setAttribute('data-theme', 'light')");
        Assert.Equal(LightSurface, await TokenAsync(dark, "--color-surface"));
    }
}
