namespace PoSeeReview.Api.Features.Comics;

/// <summary>
/// Result of one strangeness analysis call. A record rather than a
/// <c>(int, int, string)</c> tuple: the three values are all unlabelled scalars at the call
/// sites, and a positional tuple made them trivially easy to transpose.
/// </summary>
/// <param name="StrangenessScore">0-100, clamped by the service.</param>
/// <param name="PanelCount">1-2, clamped by the service.</param>
/// <param name="Narrative">Short paragraph the image model draws from.</param>
/// <param name="Captions">
/// Exactly <paramref name="PanelCount"/> narrator captions, one per panel, produced by the same
/// call as the score. Null only for a hand-constructed value (tests); every implementation
/// normalises this before returning, and callers still pass it through
/// <see cref="ChatPrompts.NormalizeCaptions"/> so the pipeline is total either way.
/// </param>
/// <param name="Scenes">
/// One illustrator brief per panel, the same panels the captions describe. Null only for a
/// hand-constructed value; callers pass it through <see cref="ChatPrompts.NormalizeScenes"/>.
/// </param>
/// <param name="Ratings">The three 0-10 judgements the score is computed from. Null for a hand-built value.</param>
/// <param name="PanelSources">
/// Per panel, the 1-based numbers of the reviews it draws on, already checked against the reviews
/// that were sent. Empty for a panel the model did not ground. Null for a hand-built value.
/// </param>
public sealed record StrangenessAnalysis(
    int StrangenessScore,
    int PanelCount,
    string Narrative,
    IReadOnlyList<string>? Captions = null,
    IReadOnlyList<string>? Scenes = null,
    StrangenessRatings? Ratings = null,
    IReadOnlyList<IReadOnlyList<int>>? PanelSources = null);

/// <summary>The model's three ratings, kept so the page can say why a score is what it is.</summary>
/// <param name="Horror">How bad living there sounds, 0-10. Apartments only; 0 for a restaurant.</param>
public sealed record StrangenessRatings(int Absurdity, int Specificity, int StoryPotential, int Horror = 0);
