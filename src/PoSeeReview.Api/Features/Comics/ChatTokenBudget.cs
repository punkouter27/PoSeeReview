using OpenAI.Chat;

using PoSeeReview.Shared.Enums;

namespace PoSeeReview.Api.Features.Comics;

/// <summary>
/// Builds the completion options every chat provider shares.
/// </summary>
internal static class ChatTokenBudget
{
    /// <summary>
    /// Constructs the shared options, applying the completion cap only where the SDK can express
    /// it correctly.
    /// <para>
    /// <b>The cap is opt-in, and on a reasoning deployment it is not applied at all.</b> That is
    /// the honest description of what this SDK allows, and it is worth stating plainly because
    /// the obvious reading of a <c>MaxCompletionTokens</c> setting is that it is in force.
    /// </para>
    /// <para>
    /// Two facts collide here. Reasoning deployments reject <c>max_tokens</c> with HTTP 400
    /// <c>unsupported_parameter</c> and require <c>max_completion_tokens</c> — that mismatch was
    /// a production outage on 2026-06-15, which is why nothing sets the typed property on that
    /// path. And the typed property is the only way this SDK version (OpenAI 2.1.0 via
    /// Azure.AI.OpenAI 2.1.0) can put a token cap on the wire at all: the options type exposes no
    /// additional-properties bag, so <c>max_completion_tokens</c> cannot be sent. A cap
    /// configured against a reasoning model therefore does nothing, and
    /// <see cref="CanApplyCap"/> is what lets startup say so out loud rather than let the setting
    /// sit in appsettings looking active.
    /// </para>
    /// <para>
    /// On a reasoning model the cap would also be the wrong instrument: reasoning tokens are
    /// billed against the same allowance, so a cap sized for the visible JSON can be spent
    /// entirely on thinking and return a completion with no content parts — a 500, not a saving.
    /// </para>
    /// </summary>
    public static ChatCompletionOptions Build(
        float temperature,
        int? maxCompletionTokens,
        bool isReasoningModel,
        PlaceCategory category = PlaceCategory.Restaurant)
    {
        var options = new ChatCompletionOptions
        {
            Temperature = temperature,
            // The schema, not json_object, for every provider: Azure enforces it, and Ollama's
            // OpenAI-compatible endpoint accepts it and constrains decoding the same way.
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                "comic_analysis", ChatPrompts.JsonSchemaFor(category), jsonSchemaIsStrict: true)
        };

        var cap = maxCompletionTokens ?? DefaultMaxCompletionTokens;
        if (CanApplyCap(cap, isReasoningModel))
        {
            options.MaxOutputTokenCount = cap;
        }

        return options;
    }

    /// <summary>
    /// The cap used when none is configured and the model family accepts one. Three ratings, a
    /// short narrative and two panels fit in a third of this; it exists so a runaway completion
    /// has a ceiling rather than the deployment's maximum.
    /// </summary>
    public const int DefaultMaxCompletionTokens = 900;

    /// <summary>
    /// Whether a configured cap will actually reach the wire: it needs a value, and it needs a
    /// model family that accepts the parameter the SDK emits.
    /// </summary>
    public static bool CanApplyCap(int? maxCompletionTokens, bool isReasoningModel) =>
        maxCompletionTokens is > 0 && !isReasoningModel;
}
