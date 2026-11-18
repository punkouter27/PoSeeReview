using Microsoft.Playwright;

namespace PoSeeReview.E2EUI;

/// <summary>
/// Verifies system-aware light/dark theming (NET_RULES 4.3, Po web standard). The page follows
/// the OS preference and nothing else: there is no theme switch, and a stray <c>data-theme</c>
/// attribute must not pin a scheme.
/// </summary>
[Collection("e2e-ui")]
[Trait("Tier", "E2EUI")]
public sealed class ThemeUiTests(PlaywrightFixture fixture)
{
    private const int RenderTimeout = 15_000;

    private const string LightSurface = "rgb(246, 247, 251)"; // --po-bg light, #f6f7fb
    private const string DarkSurface = "rgb(15, 17, 23)";     // --po-bg dark, #0f1117

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
    /// The device decides, in both directions: a <c>data-theme</c> attribute (the old Settings
    /// switch wrote one) no longer pins a scheme.
    /// </summary>
    [Theory]
    [MemberData(nameof(PlaywrightFixture.Viewports), MemberType = typeof(PlaywrightFixture))]
    public async Task Theme_DataThemeAttributeDoesNotOverrideTheOs(string viewport)
    {
        var light = await LoginPageAsync(viewport, ColorScheme.Light);
        await light.EvaluateAsync("() => document.documentElement.setAttribute('data-theme', 'dark')");
        Assert.Equal(LightSurface, await TokenAsync(light, "--color-surface"));

        var dark = await LoginPageAsync(viewport, ColorScheme.Dark);
        await dark.EvaluateAsync("() => document.documentElement.setAttribute('data-theme', 'light')");
        Assert.Equal(DarkSurface, await TokenAsync(dark, "--color-surface"));
    }
}
