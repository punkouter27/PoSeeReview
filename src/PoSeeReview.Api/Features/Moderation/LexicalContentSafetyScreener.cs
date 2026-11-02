using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using PoSeeReview.Shared.Contracts;

namespace PoSeeReview.Api.Features.Moderation;

/// <summary>
/// A lexical pre-publish screen over model-generated narratives.
/// <para>
/// <b>What this is.</b> A floor. It is word matching, it has no understanding of context or
/// negation, and it will both miss things and occasionally fire on innocent prose. It exists
/// because the alternative in place until now was nothing at all: the model wrote prose about a
/// named real business and it went straight to a user.
/// </para>
/// <para>
/// <b>Why two outcomes rather than one.</b> Blocking everything risky would break the product.
/// "Rats", "food poisoning" and "shut down by the health department" are ordinary content in a
/// one-star restaurant review — which is precisely the material this app mines — and refusing
/// them would refuse the app's best comics. But the same sentence, restated by a model as a
/// claim about a named business, is defamation-shaped. So allegation language is <b>flagged</b>:
/// it publishes and lands in the moderation queue for a human. Only the categories with no
/// legitimate reading — sexual content involving minors, slurs — are <b>blocked</b>, before the
/// paid image call.
/// </para>
/// <para>
/// <b>Replacing it.</b> Azure AI Content Safety implements this interface directly: a hosted
/// classifier returns severity per category, which maps onto Allow/Flag/Block without any caller
/// changing. That swap needs a resource, an endpoint and a key in Key Vault, so it is a
/// deployment decision rather than a code one — the seam is here and ready.
/// </para>
/// </summary>
public sealed partial class LexicalContentSafetyScreener(
    IOptions<ModerationOptions> options,
    ILogger<LexicalContentSafetyScreener> logger) : IContentSafetyScreener
{
    /// <summary>
    /// Claim-shaped language about a business: publishable, but a human should see it.
    /// <para>
    /// Word-boundary matched, so "ratatouille" does not read as "rat" — the most common way a
    /// naive filter embarrasses itself.
    /// </para>
    /// <para>
    /// Terms deliberately <em>absent</em>, because English uses them figuratively about food
    /// constantly and a queue full of false positives is a queue nobody reads: <c>assault</c>
    /// ("assaulted my expectations"), <c>stole</c> ("stole the show"), <c>drugged</c> ("drugged
    /// with sugar"), <c>killer</c>, <c>criminal</c>. Every entry below has to be a term that
    /// states a specific fact about a business rather than an intensifier.
    /// </para>
    /// </summary>
    [GeneratedRegex(
        @"\b(food[\s-]?poison(ing|ed)?|salmonella|e\.?\s?coli|norovirus|rats?|roach(es)?|vermin|maggots?|" +
        @"health\s+(code|department|inspector)|shut\s+down|closed\s+by|arrested|convicted|lawsuit|sued|" +
        @"theft|racist|racism|overdose|rape|kill\s+(yourself|himself|herself))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex AllegationPattern();

    /// <summary>
    /// Categories with no legitimate reading in a restaurant comic. Refused outright.
    /// <para>
    /// Kept deliberately narrow. Every term added here is a comic the app will refuse to draw,
    /// so the bar is "there is no sentence about a restaurant where this belongs", not "this
    /// word is unpleasant" — unpleasant is the entire premise of the product.
    /// "rape" and "kill yourself" moved to the flag list: reviews use both as hyperbole ("highway
    /// rape" prices), so they publish and reach the owner instead of refusing the comic.
    /// </para>
    /// </summary>
    [GeneratedRegex(
        @"\b(child\s+(porn|abuse|sexual)|underage\s+(sex|nude)|bestiality|" +
        @"suicide\s+(method|instructions))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex ProhibitedPattern();

    public Task<ContentScreenResult> ScreenAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!options.Value.ScreenGeneratedContent || string.IsNullOrWhiteSpace(text))
        {
            return Task.FromResult(ContentScreenResult.Allowed);
        }

        try
        {
            if (ProhibitedPattern().IsMatch(text))
            {
                logger.LogWarning("Content screen blocked a generated narrative (category: prohibited)");
                return Task.FromResult(new ContentScreenResult(ContentScreenOutcome.Block, "prohibited"));
            }

            if (AllegationPattern().IsMatch(text))
            {
                logger.LogInformation("Content screen flagged a generated narrative for review (category: allegation)");
                return Task.FromResult(new ContentScreenResult(ContentScreenOutcome.Flag, "allegation"));
            }
        }
        catch (RegexMatchTimeoutException ex)
        {
            // A pathological narrative must not stall the pipeline. Allowing is the right
            // failure here for the same reason the verdict lookup fails open: a timeout is a
            // property of the input's shape, not evidence of what it says.
            logger.LogWarning(ex, "Content screen timed out; allowing the narrative through");
        }

        return Task.FromResult(ContentScreenResult.Allowed);
    }
}
