using System.Text.Json;
using OpenAI.Chat;
using PoSeeReview.Shared.Enums;

namespace PoSeeReview.Api.Features.Comics;

/// <summary>
/// The one analysis call body, shared by every chat provider (Azure OpenAI, Ollama) — messages,
/// response schema, empty-completion guard, JSON parse and usage accounting. A provider supplies
/// its <see cref="ChatClient"/> and its token budget, and nothing else.
/// </summary>
internal static class OpenAiWireChat
{
    /// <summary>One analysis call plus what it cost in tokens.</summary>
    internal sealed record Result(StrangenessAnalysis Analysis, long InputTokens, long OutputTokens, long CachedInputTokens);

    public static async Task<Result> AnalyzeAsync(
        ChatClient client,
        List<string> reviews,
        float temperature,
        int? maxCompletionTokens,
        bool isReasoningModel,
        PlaceCategory category,
        CancellationToken cancellationToken)
    {
        var validReviews = (reviews ?? []).Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
        if (validReviews.Count == 0)
        {
            throw new ArgumentException("No valid reviews provided", nameof(reviews));
        }

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(ChatPrompts.SystemMessageFor(category)),
            new UserChatMessage(ChatPrompts.BuildAnalysisPrompt(validReviews))
        };

        var response = await client.CompleteChatAsync(
            messages,
            ChatTokenBudget.Build(temperature, maxCompletionTokens, isReasoningModel, category),
            cancellationToken);

        var completion = response.Value;

        // A completion with no parts is not hypothetical: a content-filtered completion, or a
        // reasoning deployment whose budget is spent thinking, returns zero content parts, and
        // indexing [0] turned that into an exception pointing at the SDK rather than the model.
        if (completion.Content.Count == 0)
        {
            throw new InvalidOperationException("Chat provider returned an empty completion.");
        }

        var result = DeserializeLenient<StrangenessAnalysisResult>(completion.Content[0].Text)
            ?? throw new InvalidOperationException("Failed to parse chat response");

        return new Result(
            ChatPrompts.ToAnalysis(result, validReviews.Count, category),
            completion.Usage?.InputTokenCount ?? 0,
            completion.Usage?.OutputTokenCount ?? 0,
            completion.Usage?.InputTokenDetails?.CachedTokenCount ?? 0);
    }

    /// <summary>
    /// Tolerant JSON parse, the fallback for a model that ignores the schema: open models
    /// sometimes wrap JSON in a ```json fence, or bracket it with a sentence of preamble.
    /// </summary>
    private static T? DeserializeLenient<T>(string content) where T : class
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var text = content.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = text.IndexOf('\n');
            if (firstNewline >= 0)
            {
                text = text[(firstNewline + 1)..];
            }

            text = text.TrimEnd('`', '\n', '\r', ' ').TrimEnd('`');
        }

        var start = text.IndexOfAny(['{', '[']);
        var end = text.LastIndexOfAny(['}', ']']);
        if (start >= 0 && end > start)
        {
            text = text[start..(end + 1)];
        }

        return JsonSerializer.Deserialize<T>(text, LenientJsonOptions);
    }

    /// <summary>
    /// Case-insensitive on purpose: the prompt shows the model a JSON shape and the model
    /// copies the key casing it was shown, so the wire casing is whatever the prompt says it is
    /// — not whatever the POCO says. Default (strict) options read a camelCase reply into a
    /// PascalCase POCO as all-null, which surfaces as "empty completion" far from the cause.
    /// </summary>
    private static readonly JsonSerializerOptions LenientJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        // A small model writes "reviewIds": ["1"] as often as [1]; a quoted number is still a number.
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };
}
