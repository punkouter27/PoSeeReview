namespace PoSeeReview.Shared.Contracts;

/// <summary>
/// The scorer's three 0-10 ratings behind a comic's score. Kept so the page can say why a place
/// scored what it did; absent on comics drawn before they were stored.
/// </summary>
public sealed record ComicScoreBreakdown(int Absurdity, int Specificity, int StoryPotential, int Horror = 0);

/// <summary>
/// One review a panel was drawn from: which panel, the review's position in what the scorer was
/// shown, and its star rating. Deliberately no text and no author — the page links to the listing
/// instead of quoting, so there is nothing to misquote and no reviewer singled out.
/// </summary>
public sealed record ComicSource(int Panel, int ReviewNumber, int Rating);
