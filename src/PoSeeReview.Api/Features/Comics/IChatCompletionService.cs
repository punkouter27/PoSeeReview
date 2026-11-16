using PoSeeReview.Shared.Enums;

namespace PoSeeReview.Api.Features.Comics;

/// <summary>
/// Scores restaurant reviews for strangeness, and writes the narrative and the panel captions
/// that follow from that score. Implemented per provider (Azure OpenAI, Ollama);
/// the prompt contract they share lives in <see cref="ChatPrompts"/>.
/// <para>
/// <b>One method, on purpose.</b> There used to be a second — <c>GeneratePanelDialogueAsync</c> —
/// for panel captions, called by the overlay step after the image had been drawn. It has been
/// folded into this call and deleted rather than kept as a convenience: a caption and a
/// narrative are the same story told at two lengths, and computing them from one response is
/// the only way to guarantee they agree. A second entry point would invite the drift back.
/// </para>
/// </summary>
public interface IChatCompletionService
{
    /// <summary>
    /// Analyzes a list of restaurant reviews to determine strangeness and create a narrative
    /// </summary>
    /// <param name="reviews">List of review texts to analyze</param>
    /// <param name="category">Which rubric scores them: restaurant strangeness, or apartment strangeness and horror</param>
    /// <param name="cancellationToken">Cancels the (potentially slow) model call when the caller abandons the request</param>
    /// <returns>Score, panel count, narrative, and one caption per panel.</returns>
    /// <exception cref="ArgumentException">If reviews list is empty</exception>
    Task<StrangenessAnalysis> AnalyzeStrangenessAsync(List<string> reviews, PlaceCategory category = PlaceCategory.Restaurant, CancellationToken cancellationToken = default);

}
