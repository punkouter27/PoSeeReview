namespace PoSeeReview.Api.Features.Comics;

/// <summary>
/// Configuration options for comic generation business rules.
/// Centralises constants that were previously hard-coded in the generation pipeline so they
/// are runtime-configurable without a redeploy. The leaderboard threshold lives in
/// LeaderboardOptions, owned by that slice.
/// </summary>
public class ComicOptions
{
    public const string SectionName = "Comics";

    /// <summary>
    /// Minimum number of raw reviews needed before a comic can be generated, also applied to
    /// the surviving set after inappropriate-content filtering. 1 — the default — is the floor:
    /// a comic needs at least one review to draw from, so this is as permissive as the pipeline
    /// allows. Raise it to demand more source material per comic.
    /// </summary>
    public int MinimumReviewsRequired { get; set; } = 1;

    /// <summary>
    /// Maximum number of reviews forwarded to GPT for strangeness analysis
    /// (controls cost — fewer reviews = cheaper prompt).
    /// </summary>
    public int MaximumReviewsForAnalysis { get; set; } = 5;

    /// <summary>
    /// How many days a generated comic is cached before it is considered stale.
    /// </summary>
    public int CacheDurationDays { get; set; } = 7;

    /// <summary>
    /// Minimum strangeness score (0–100) a restaurant must reach before a comic is generated;
    /// anything lower is rejected as too ordinary (422) before the paid image call, and the
    /// reserved budget unit is refunded. 15 sits just above what an ordinary complaint scores, so
    /// "the food was cold" no longer buys an image. 0 disables the gate. The leaderboard's own
    /// admission threshold lives in LeaderboardOptions and is unaffected.
    /// </summary>
    public int MinimumStrangenessScore { get; set; } = 15;

    /// <summary>
    /// Version stamp for the prompt and model behind a comic, folded into the cache check.
    /// <para>
    /// The cache is keyed by place and age alone, so a prompt tune or a model swap kept serving
    /// the old output until somebody paid for a forced regeneration — which made evaluating a
    /// prompt change cost one generation per restaurant tested, and made it easy to compare a
    /// new prompt against an old comic and conclude the wrong thing. Bump this whenever the
    /// prompt, the rubric or the model changes; rows written under a different version simply
    /// miss the cache and are regenerated on demand, with no migration and no bulk job.
    /// </para>
    /// </summary>
    public int PromptVersion { get; set; } = 1;

    /// <summary>
    /// What happens to review text before the scorer sees it. <see cref="ReviewFilter.None"/>
    /// (the default): the reviews go in as written, swearing and all — the owner wants the app
    /// as uncensored as it can be, and anything a model then refuses is reported, not hidden.
    /// </summary>
    public ReviewFilter ReviewFilter { get; set; } = ReviewFilter.None;

    /// <summary>
    /// Swap image-filter trigger words ("blood", "rats", "drugs") for "unusual" before the image
    /// call. Off by default: the scene is drawn as written, and a refusal is shown to the user
    /// and pushed to the owner. On, every swap is still pushed to the owner.
    /// </summary>
    public bool BluntImagePrompts { get; set; }
}

/// <summary>How much of a review is kept from the scorer.</summary>
public enum ReviewFilter
{
    /// <summary>As written.</summary>
    None,

    /// <summary>Profanity becomes "[bleep]"; nothing is dropped.</summary>
    Mask,

    /// <summary>Profanity masked, and a review with an explicit term is dropped (the old behaviour).</summary>
    Strict
}
