namespace PoSeeReview.Api.Features.Comics;

/// <summary>
/// Thrown when the pre-publish content screen refuses a generated narrative.
/// <para>
/// Raised before the image model is called, so nothing was spent and the caller's budget unit is
/// refundable — the same treatment the strangeness floor gets, and for the same reason.
/// </para>
/// <para>
/// The message names the category. The owner wants every refusal visible; a vague "could not be
/// published" hid what the screen had done.
/// </para>
/// </summary>
public sealed class ContentBlockedException(string category)
    : Exception($"The story drawn from these reviews was blocked by the content screen ({category}), so nothing was drawn.")
{
    /// <summary>The screen category that refused it.</summary>
    public string Category { get; } = category;
}
