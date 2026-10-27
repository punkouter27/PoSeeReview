using System.Text.RegularExpressions;

namespace PoSeeReview.Api.Features.Comics;

/// <summary>
/// The positive-only image prompt shared by the painters that have no negative-prompt channel
/// (Gemini, Azure gpt-image).
/// <para>
/// <b>The panel breakdown is the chat model's shot list, and it is the whole story.</b> Each
/// numbered line is the scene its caption describes. The narrative is deliberately not quoted
/// alongside: the scenes already carry it, and a quoted paragraph is an invitation to letter it
/// into the artwork.
/// </para>
/// </summary>
internal static partial class ComicImagePrompt
{
    /// <summary>
    /// Terms the image safety filters reject. The analysis prompt already asks for scenes in
    /// cartoon terms, so this is the backstop, not the plan: every hit is reported by the caller.
    /// <para>
    /// Whole words with their inflections spelled out. A trailing <c>\w*</c> used to turn
    /// "ratatouille", "rating", "rather", "diet" and "method" into "unusual".
    /// </para>
    /// </summary>
    [GeneratedRegex(
        @"\b(?:blood(?:y|ied)?|kill(?:s|ed|ing|ers?)?|murder(?:s|ed|ing|ers?|ous)?|dead(?:ly)?|deaths?|die[sd]?|dying|" +
        @"guns?|gun(?:shots?|fire|m[ae]n)|shoot(?:s|ing|ings|ers?)?|weapons?|kni(?:fe|ves|fed)|stab(?:s|bed|bing)?|" +
        @"fight(?:s|ing|ers?)?|attack(?:s|ed|ing|ers?)?|drug(?:s|ged)?|cocaine|heroin|meth|naked|nud(?:e|es|ity)|" +
        @"sex(?:y|ual|ually)?|hat(?:e|es|ed|ing|eful|red)|racis[tm]s?|racial(?:ly)?|vomit(?:s|ed|ing)?|puk(?:e|es|ed|ing)|" +
        @"disgust(?:s|ed|ing)?|(?:cock)?roach(?:es)?|rats?|mice|vermin|poison(?:s|ed|ing|ous)?|toxic|" +
        @"contaminat(?:e|es|ed|ing|ion))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FlaggedTermRegex();

    /// <summary>Builds the prompt, blunting flagged terms, and reports which ones it blunted.</summary>
    /// <param name="filmStills">
    /// For FLUX: to it "comic strip" means speech balloons, which it fills with misspelled
    /// lettering whatever the rest of the prompt says. Framed as stills from a silent cartoon,
    /// with closed mouths and bare walls, it draws none.
    /// </param>
    public static (string Prompt, List<string> BluntedTerms) Build(IReadOnlyList<string> scenes, bool filmStills = false)
    {
        var blunted = new List<string>();
        var breakdown = string.Join("\n", scenes.Select((scene, i) => $"{i + 1}. {Blunt(scene, blunted)}"));
        var panelCount = scenes.Count;

        if (filmStills)
        {
            var frames = panelCount == 1
                ? "One wide landscape cartoon illustration, like a still frame from a silent animated short film."
                : "Two stacked landscape cartoon illustrations, like two still frames from a silent animated short film, separated by a thick black border. The same characters, with matching outfits and visual traits, appear in both frames.";

            return ($"""
{frames}

Draw each frame exactly as described:
{breakdown}

Visual style:
- Bold outlines, vivid colors, modern cartoon illustration (not manga, not realistic)
- Exaggerated facial expressions and body language carry every emotion
- Characters react with closed-mouth expressions, eye-rolls, shrugs and pointing; nobody is talking
- Bare walls in plain solid colors; signs, menus and posters are blank colored shapes
- Clean empty background space above the characters' heads
""", blunted);
        }

        var panelLayout = panelCount == 1
            ? "Single-panorama comic strip (one wide scene filling the frame)"
            : "Two-panel comic strip with equal landscape panels stacked vertically";

        // No negative-prompt channel: forbidden concepts named in the prompt ("NO SPEECH
        // BUBBLES") tend to get PAINTED INTO the artwork as literal lettering. Describe only what
        // we want — wordless, pantomime, blank surfaces — and never mention text, bubbles, or
        // writing. Captions are added later by the overlay service.
        var prompt = $"""
Create a vibrant {panelCount}-panel wordless pantomime comic strip in a clean, modern cartoon illustration style, told purely through pictures, in the tradition of silent-film slapstick.

Layout: {panelLayout}
- Consistent characters across panels with matching outfits and visual traits
- Clean black panel gutters/borders separating EXACTLY {panelCount} panel(s)

Draw each panel exactly as described:
{breakdown}

Visual style:
- Bold outlines, vivid colors, exaggerated facial expressions and body language
- Modern cartoon illustration (NOT manga, NOT realistic)
- Pure visual storytelling: every emotion carried by faces, gestures, and posture alone
- Every wall, sign, menu, and surface rendered as plain solid color or simple decoration
- Wordless, silent, pantomime scenes throughout
""";

        return (prompt, blunted);
    }

    private static string Blunt(string text, List<string> blunted) =>
        FlaggedTermRegex().Replace(text, match =>
        {
            var term = match.Value.ToLowerInvariant();
            if (!blunted.Contains(term, StringComparer.Ordinal))
            {
                blunted.Add(term);
            }

            return "unusual";
        });
}
