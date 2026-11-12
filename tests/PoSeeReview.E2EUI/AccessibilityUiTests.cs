using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;

namespace PoSeeReview.E2EUI;

/// <summary>
/// axe-core over the main pages, failing on serious or critical WCAG 2.1 A/AA violations.
/// <para>
/// ColorContrastTests proves the tokens against each other; this proves the rendered pages —
/// a missing label, a button with no name, a token paired with the wrong surface in markup.
/// Reduced motion is emulated so entrance animations (opacity mid-fade) cannot fake a contrast
/// failure, and it also pins the effects tier to off.
/// </para>
/// </summary>
[Collection("e2e-ui")]
[Trait("Tier", "E2EUI")]
public sealed class AccessibilityUiTests(PlaywrightFixture fixture)
{
    private const int RenderTimeout = 30_000;
    private static readonly System.Text.RegularExpressions.Regex GuestButton =
        new("continue as guest", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly AxeRunOptions WcagAa = new()
    {
        RunOnly = new RunOnlyOptions { Type = "tag", Values = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"] }
    };

    [Theory]
    [InlineData("/login", ".login-container", null)]
    [InlineData("/", ".index-container", null)]
    [InlineData("/?q=Seattle", ".restaurants-grid [data-testid='restaurant-card']", "list")]
    // Headless Chromium without GPU flags has no WebGL, so this audits the map OR the list it falls back to.
    [InlineData("/?q=Seattle", ".map-pin, .restaurants-grid", "map")]
    [InlineData("/leaderboard", "#leaderboard-results[aria-busy='false']", null)]
    [InlineData("/leaderboard?view=week", "#leaderboard-results[aria-busy='false']", null)]
    [InlineData("/my-comics", "main", null)]
    [InlineData("/settings", "[data-testid='settings-sound']", null)]
    public async Task Page_HasNoSeriousAccessibilityViolations(string route, string readySelector, string? discoveryLayout)
    {
        var page = await fixture.NewPageAsync("desktop");
        await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });

        if (route != "/login")
        {
            await page.GotoAsync($"{fixture.BaseUrl}/login");
            await page.GetByRole(AriaRole.Button, new() { NameRegex = GuestButton }).ClickAsync(new() { Timeout = RenderTimeout });
            await page.Locator(".nav-user-zone").WaitForAsync(new() { Timeout = RenderTimeout });
        }

        if (discoveryLayout is not null)
        {
            await page.EvaluateAsync("layout => localStorage.setItem('poseereview:discovery-layout', layout)", discoveryLayout);
        }

        await page.GotoAsync($"{fixture.BaseUrl}{route}");
        await page.Locator(readySelector).First.WaitForAsync(new() { Timeout = RenderTimeout });
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var result = await page.RunAxe(WcagAa);

        var serious = result.Violations
            .Where(v => v.Impact is "serious" or "critical")
            .Select(v => $"{v.Id} ({v.Impact}): {v.Help} — {string.Join(", ", v.Nodes.Take(3).Select(n => n.Target?.ToString()))}")
            .ToList();

        Assert.True(serious.Count == 0, $"{route}:\n{string.Join("\n", serious)}");
    }
}
