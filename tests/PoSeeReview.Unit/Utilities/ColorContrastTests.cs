using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;

namespace PoSeeReview.Unit.Utilities;

/// <summary>
/// Guards the palette against WCAG regressions by parsing the real token values out of tokens.css,
/// resolving its <c>var(--po-*)</c> aliases against po-tokens.css (the Po web standard's estate
/// tokens, which supply the colours since the 2026-11 standard).
/// <para>
/// This exists because the light palette silently shipped failing contrast: <c>--color-accent</c>
/// measured 2.15:1 on a white card and <c>--color-success</c> 2.54:1, against a 4.5:1 minimum,
/// and <c>--color-border</c> was 1.22:1 — not a visible edge at all. Nothing caught it, because
/// the existing theme tests assert token VALUES rather than the contrast those values produce.
/// </para>
/// <para>
/// Reading the stylesheet rather than duplicating the hex codes here is deliberate: a test that
/// restates the values it is checking passes forever no matter what the app actually ships.
/// </para>
/// </summary>
[Trait("Tier", "Unit")]
[Trait("Suite", "CriticalPath")]
public class ColorContrastTests
{
    private const double AaNormalText = 4.5;   // WCAG 1.4.3
    private const double AaUiComponent = 3.0;  // WCAG 1.4.11

    private static readonly Lazy<string> CssDir = new(() =>
    {
        // Walk up from the test binary to the repo root; the client's stylesheets are the source
        // of truth for every token in the app.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PoSeeReview.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir is not null, "Could not locate the repository root from the test output directory.");
        return Path.Combine(dir!.FullName, "src", "PoSeeReview.Client", "wwwroot", "css");
    });

    private static readonly Lazy<string> AppCss = new(() => ReadCss("tokens.css"));
    private static readonly Lazy<string> PoCss = new(() => ReadCss("po-tokens.css"));

    private static string ReadCss(string name)
    {
        var path = Path.Combine(CssDir.Value, name);
        Assert.True(File.Exists(path), $"{name} not found at {path}");
        return File.ReadAllText(path);
    }

    /// <summary>The body of the first <c>{ … }</c> block opened at or after <paramref name="anchor"/>.</summary>
    private static string Block(string css, string anchor)
    {
        var start = css.IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find the block anchored at '{anchor}'.");
        var open = css.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < css.Length; i++)
        {
            depth += css[i] switch { '{' => 1, '}' => -1, _ => 0 };
            if (depth == 0)
            {
                return css[(open + 1)..i];
            }
        }

        return css[(open + 1)..];
    }

    private static string? Declared(string block, string token)
    {
        var match = Regex.Match(block, $@"{Regex.Escape(token)}\s*:\s*([^;]+);");
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    /// <summary>
    /// A token's colour in one theme. The dark block restates only the app's own literals, so a
    /// token it does not declare falls back to the light declaration; a <c>var(--po-*)</c> alias
    /// is then read from po-tokens.css's matching block (light <c>:root</c> or the dark media query).
    /// </summary>
    private static string ReadToken(string token, string blockAnchor)
    {
        var dark = blockAnchor == DarkBlock;
        var value = (dark ? Declared(Block(AppCss.Value, DarkBlock), token) : null)
                    ?? Declared(Block(AppCss.Value, LightBlock), token);
        Assert.True(value is not null, $"Token {token} not found in tokens.css.");

        var alias = Regex.Match(value!, @"^var\((--po-[\w-]+)\)$");
        if (alias.Success)
        {
            var poToken = alias.Groups[1].Value;
            value = (dark ? Declared(Block(PoCss.Value, DarkBlock), poToken) : null)
                    ?? Declared(Block(PoCss.Value, ":root"), poToken);
            Assert.True(value is not null, $"{token} aliases {poToken}, which po-tokens.css does not declare.");
        }

        Assert.Matches("^#[0-9a-fA-F]{3,8}$", value);
        return value!;
    }

    private static double RelativeLuminance(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 3)
        {
            hex = string.Concat(hex.Select(c => new string(c, 2)));
        }

        double Channel(int offset)
        {
            var v = int.Parse(hex.AsSpan(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(0) + 0.7152 * Channel(2) + 0.0722 * Channel(4);
    }

    private static double ContrastRatio(string a, string b)
    {
        var (la, lb) = (RelativeLuminance(a), RelativeLuminance(b));
        var (hi, lo) = (Math.Max(la, lb), Math.Min(la, lb));
        return (hi + 0.05) / (lo + 0.05);
    }

    // The light palette is the one that regressed; the layer's :root is its declaration block.
    private const string LightBlock = "@layer tokens {";
    private const string DarkBlock = "@media (prefers-color-scheme: dark)";

    private static readonly string[] TextTokens =
    [
        "--color-text-primary",
        "--color-text-secondary",
        "--color-text-muted",
        "--color-brand-ink",
        "--color-accent-ink",
        "--color-success-ink",
        "--color-danger",
    ];

    /// <summary>
    /// Every surface token a text token can legitimately land on — not just <c>--color-card</c>.
    /// Checking only the card is what let <c>--color-text-muted</c> ship at 4.26:1: it cleared
    /// 4.5:1 against pure white, but the Hall of Fame timestamp renders on
    /// <c>--color-brand-surface</c>, and axe caught in the browser what this test could not.
    /// </summary>
    public static TheoryData<string, string> LightTextOnSurface => Pairs(TextTokens,
    [
        "--color-card",
        "--color-surface",
        "--color-surface-alt",
        "--color-brand-surface",
        "--color-highlight",
    ]);

    public static TheoryData<string, string> DarkTextOnSurface => Pairs(TextTokens,
    [
        "--color-card",
        "--color-surface",
        "--color-surface-alt",
        "--color-brand-surface",
        "--color-highlight",
    ]);

    private static TheoryData<string, string> Pairs(string[] tokens, string[] surfaces)
    {
        var data = new TheoryData<string, string>();
        foreach (var token in tokens)
        {
            foreach (var surface in surfaces)
            {
                data.Add(token, surface);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(LightTextOnSurface))]
    public void LightMode_TextTokens_MeetAaOnEverySurface(string token, string surfaceToken)
    {
        AssertReadable(token, surfaceToken, LightBlock, "light");
    }

    [Theory]
    [MemberData(nameof(DarkTextOnSurface))]
    public void DarkMode_TextTokens_MeetAaOnEverySurface(string token, string surfaceToken)
    {
        AssertReadable(token, surfaceToken, DarkBlock, "dark");
    }

    private static void AssertReadable(string token, string surfaceToken, string block, string theme)
    {
        var surface = ReadToken(surfaceToken, block);
        var value = ReadToken(token, block);
        var ratio = ContrastRatio(value, surface);

        Assert.True(ratio >= AaNormalText,
            $"{theme}: {token} ({value}) on {surfaceToken} ({surface}) is {ratio:F2}:1 — " +
            $"below the {AaNormalText}:1 AA minimum for normal text.");
    }

    [Fact]
    public void AccentSurface_PairedWithItsInk_IsReadable()
    {
        // The point of the surface/ink split: bright amber stays bright, and is made usable by
        // pairing it with dark ink rather than by darkening the brand colour.
        var accent = ReadToken("--color-accent", LightBlock);
        var onAccent = ReadToken("--color-on-accent", LightBlock);
        var ratio = ContrastRatio(onAccent, accent);

        Assert.True(ratio >= AaNormalText,
            $"--color-on-accent ({onAccent}) on --color-accent ({accent}) is {ratio:F2}:1.");
    }

    [Theory]
    [InlineData(LightBlock)]
    [InlineData(DarkBlock)]
    public void BorderStrong_IsAPerceivableUiBoundary(string block)
    {
        // 1.4.11: a control boundary needs 3:1. The old single --color-border was 1.22:1, which
        // is why inputs and secondary buttons had no visible edge in light mode.
        var card = ReadToken("--color-card", block);
        var border = ReadToken("--color-border-strong", block);
        var ratio = ContrastRatio(border, card);

        Assert.True(ratio >= AaUiComponent,
            $"--color-border-strong ({border}) on {card} is {ratio:F2}:1 — below the {AaUiComponent}:1 minimum for UI components.");
    }

    [Fact]
    public void OnDarkText_IsReadableAgainstTheInverseSurface()
    {
        // The nav bar and hero are dark in BOTH themes, so they are checked once against the
        // inverse surface rather than per theme.
        var surface = ReadToken("--surface-inverse", LightBlock);
        var text = ReadToken("--color-on-dark", LightBlock);
        var ratio = ContrastRatio(text, surface);

        Assert.True(ratio >= AaNormalText,
            $"--color-on-dark ({text}) on --surface-inverse ({surface}) is {ratio:F2}:1.");
    }
}
