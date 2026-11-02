using PoSeeReview.Shared.Ids;

namespace PoSeeReview.Shared.Contracts;

/// <summary>
/// What moderation currently says about one place.
/// </summary>
/// <param name="IsHidden">
/// The comic exists but must not be served. Reversible, and used while a report is being looked
/// at — the alternative, deleting on the strength of an unreviewed report, makes every report a
/// deletion API.
/// </param>
/// <param name="IsSuppressed">
/// No comic may be generated for this place at all. This is the state a completed takedown
/// leaves behind: without it, deleting a comic accomplishes nothing, because the next visitor
/// regenerates the same comic about the same named business on the next tap.
/// </param>
/// <param name="Reason">Operator-facing note. Never rendered to end users.</param>
public sealed record ModerationVerdict(bool IsHidden, bool IsSuppressed, string? Reason)
{
    /// <summary>Nothing on record — the overwhelmingly common case.</summary>
    public static readonly ModerationVerdict Clear = new(false, false, null);

    /// <summary>True when the place is neither hidden nor suppressed.</summary>
    public bool IsServable => !IsHidden && !IsSuppressed;
}

/// <summary>
/// The moderation state of a place, as the rest of the app sees it.
/// <para>
/// Declared in Shared because three slices need it and none of them may reference the Moderation
/// slice that owns it (NET_RULES 2.2): Comics asks before serving or generating, Reports feeds it
/// report counts, and Takedowns marks a place suppressed once content has come down. Everything
/// else — the queue, the audit fields, the operator endpoints — stays inside the slice.
/// </para>
/// </summary>
public interface IContentModerationGate
{
    /// <summary>
    /// Reads the current verdict. Called on read and generation paths, so an unreachable store
    /// resolves to <see cref="ModerationVerdict.Clear"/> rather than taking the app down —
    /// failing closed here would make a storage blip look like a global outage.
    /// </summary>
    Task<ModerationVerdict> EvaluateAsync(PlaceId placeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records how many distinct people have now reported a place, auto-hiding it once the
    /// configured threshold is crossed.
    /// </summary>
    /// <returns>True when this call is what hid the comic.</returns>
    Task<bool> RecordReportAsync(PlaceId placeId, int distinctReporters, CancellationToken cancellationToken = default);

    /// <summary>
    /// Blocks any future generation for this place. Idempotent.
    /// </summary>
    Task SuppressAsync(PlaceId placeId, string reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts a place in front of a moderator without withholding it.
    /// <para>
    /// This is what the pre-publish content screen does when generated prose reads as a claim
    /// about a named business — language that is ordinary in a one-star review and risky when a
    /// model restates it. Hiding on that signal alone would refuse the app's best comics; doing
    /// nothing leaves the riskiest ones unseen.
    /// </para>
    /// </summary>
    Task FlagForReviewAsync(PlaceId placeId, string category, CancellationToken cancellationToken = default);
}

/// <summary>
/// Category prefixes for <see cref="IContentModerationGate.FlagForReviewAsync"/> that mark a
/// comic as kept off the app or edited before it got there, rather than merely flagged. The
/// owner is alerted on every one of them.
/// </summary>
public static class ModerationCategories
{
    /// <summary>The content screen refused it before the image call. "blocked: {category}".</summary>
    public const string Blocked = "blocked";

    /// <summary>The image model refused to draw it. "image_declined: {reason}".</summary>
    public const string ImageDeclined = "image_declined";

    /// <summary>Words were swapped out of the image prompt. "prompt_softened: {terms}".</summary>
    public const string PromptSoftened = "prompt_softened";

    /// <summary>The voice model refused a line of the audio comic. "voice_declined: {reason}".</summary>
    public const string VoiceDeclined = "voice_declined";

    public static string Describe(string prefix, string detail) => $"{prefix}: {detail}";
}
