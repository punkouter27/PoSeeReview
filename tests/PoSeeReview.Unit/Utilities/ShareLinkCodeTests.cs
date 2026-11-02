using System.Reflection;
using Xunit;

namespace PoSeeReview.Unit.Utilities;

/// <summary>
/// Short-link code minting.
/// <para>
/// <c>ShareLinkKeys</c> is internal to the API assembly, so this reaches it by reflection rather
/// than widening its visibility for a test. The properties being guarded are cheap to state and
/// expensive to discover in production: a code with an ambiguous glyph fails silently for
/// whoever retypes it off a screenshot, and a predictable one turns every shared comic into an
/// enumerable list.
/// </para>
/// </summary>
[Trait("Tier", "Unit")]
[Trait("Suite", "CriticalPath")]
public class ShareLinkCodeTests
{
    private static readonly Type Keys =
        typeof(PoSeeReview.Api.Storage.AzureStorageOptions).Assembly
            .GetType("PoSeeReview.Api.Features.Sharing.ShareLinkKeys", throwOnError: true)!;

    private static string NewCode() =>
        (string)Keys.GetMethod("NewCode", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!;

    private static bool IsWellFormed(string? code) =>
        (bool)Keys.GetMethod("IsWellFormed", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [code])!;

    [Fact]
    public void NewCode_ProducesWellFormedCodes()
    {
        for (var i = 0; i < 200; i++)
        {
            Assert.True(IsWellFormed(NewCode()));
        }
    }

    [Fact]
    public void NewCode_AvoidsGlyphsThatCannotBeRetypedReliably()
    {
        // 0/O and 1/I/l are indistinguishable in most UI fonts. These codes get read aloud and
        // copied off screenshots, so an ambiguous one is a link that 404s for reasons the person
        // holding it cannot see.
        var sample = string.Concat(Enumerable.Range(0, 400).Select(_ => NewCode()));

        Assert.DoesNotContain('0', sample);
        Assert.DoesNotContain('O', sample);
        Assert.DoesNotContain('1', sample);
        Assert.DoesNotContain('I', sample);
        Assert.DoesNotContain('l', sample);
        Assert.DoesNotContain('B', sample);
    }

    [Fact]
    public void NewCode_DoesNotRepeatOverAModestSample()
    {
        // Not a uniqueness proof — the repository still treats a 409 as the real collision check.
        // This catches the failure mode where minting is accidentally deterministic.
        var codes = Enumerable.Range(0, 1000).Select(_ => NewCode()).ToList();

        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("waytoolongforacode")]
    [InlineData("abcd3f0")]   // contains an excluded glyph
    [InlineData("abc/def")]
    public void IsWellFormed_RejectsAnythingItCouldNotHaveMinted(string? candidate) =>
        // The resolver calls this before touching storage: a public, unauthenticated endpoint
        // must not let a wrong guess turn into a table read.
        Assert.False(IsWellFormed(candidate));
}
