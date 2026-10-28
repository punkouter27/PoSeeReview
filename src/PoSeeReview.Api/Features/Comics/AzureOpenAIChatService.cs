using PoSeeReview.Api.Telemetry;
using Azure.AI.OpenAI;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Options;

namespace PoSeeReview.Api.Features.Comics;

/// <summary>
/// Azure AI Foundry backend for strangeness analysis, using the <c>gpt-5.4-nano</c> deployment
/// in <c>po-aiservices-shared2</c>. The call itself is <see cref="OpenAiWireChat"/>; transient
/// failures are retried by the SDK's own pipeline, so there is no second retry policy here.
/// </summary>
public class AzureOpenAIChatService : IChatCompletionService
{
    /// <summary>Telemetry label for this provider, and the key <c>AiPricing:Models</c> is read with.</summary>
    public const string ProviderLabel = "AzureOpenAI";

    private readonly AzureOpenAIClient _openAIClient;
    private readonly AzureOpenAIOptions _options;
    private readonly TelemetryClient _telemetryClient;
    private readonly AiCostTracker _costTracker;

    public AzureOpenAIChatService(
        AzureOpenAIClient openAIClient,
        IOptions<AzureOpenAIOptions> options,
        TelemetryClient telemetryClient,
        AiCostTracker costTracker)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(_options.DeploymentName))
        {
            throw new InvalidOperationException("AzureOpenAI deployment name not configured");
        }

        // Injected via DI so the underlying transport can be swapped for a mock in
        // test environments (AddMockedAiBoundaries) — no real Foundry calls / token spend.
        _openAIClient = openAIClient ?? throw new ArgumentNullException(nameof(openAIClient));
        _telemetryClient = telemetryClient ?? throw new ArgumentNullException(nameof(telemetryClient));
        _costTracker = costTracker ?? throw new ArgumentNullException(nameof(costTracker));
    }

    /// <inheritdoc />
    public async Task<StrangenessAnalysis> AnalyzeStrangenessAsync(List<string> reviews, CancellationToken cancellationToken = default)
    {
        if (reviews == null || reviews.Count == 0)
            throw new ArgumentException("Reviews list cannot be empty", nameof(reviews));

        // Temperature 0.3 for a repeatable score. The cap only reaches the wire on an instruct
        // deployment — see ChatTokenBudget.
        var result = await OpenAiWireChat.AnalyzeAsync(
            _openAIClient.GetChatClient(_options.DeploymentName),
            reviews,
            temperature: 0.3f,
            maxCompletionTokens: _options.MaxCompletionTokens,
            isReasoningModel: _options.IsReasoningModel,
            cancellationToken);

        _telemetryClient.GetMetric("AzureOpenAI.Chat.Requests").TrackValue(1);
        _costTracker.Track(ProviderLabel, _options.DeploymentName, result.InputTokens, result.OutputTokens, result.CachedInputTokens);

        return result.Analysis;
    }
}
