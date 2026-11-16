using PoSeeReview.Api.Telemetry;
using System.ClientModel;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;

using PoSeeReview.Shared.Enums;

namespace PoSeeReview.Api.Features.Comics;

/// <summary>
/// Local Ollama backend for strangeness analysis, used when <c>Ai:ChatProvider</c> is
/// <c>Ollama</c>.
/// <para>
/// <b>Why a local tier is worth the wiring.</b> Scoring five short reviews is a small instruct
/// task, not a reasoning task. Running it against a paid endpoint means the only way to exercise
/// the comic pipeline is to spend money, so the pipeline is exercised rarely and late. A local
/// model makes the whole path — prompt, parse, clamp, caption, overlay — testable at zero cost.
/// It is selected by configuration only; nothing falls back to it at runtime.
/// </para>
/// <para>
/// <b>Why not WebGPU in the browser instead.</b> The client is the wrong place for this and it
/// is worth recording the verdict where someone would look for it. The client already spends its
/// whole GPU budget on the effects layer under a 20ms frame budget with an automatic downgrade;
/// <c>webgpu-pool.js</c> is deliberately scoped to effects where <em>compute</em> changes what
/// the effect can be, not where it would be marginally faster. A scorer in the client would
/// also have to ship the review text to the browser to be scored there, and would produce a
/// score that only that one device agrees with. Server-side, either hosted or local, is where
/// this belongs.
/// </para>
/// </summary>
public sealed class OllamaChatService : IChatCompletionService
{
    /// <summary>
    /// Label used for telemetry. Matches the <c>FreeProviders</c> entry in
    /// <see cref="AiPricingOptions"/> so a local call reports zero dollars rather than a
    /// made-up one.
    /// </summary>
    public const string ProviderLabel = "Ollama";

    private readonly ChatClient _chatClient;
    private readonly OllamaOptions _options;
    private readonly ILogger<OllamaChatService> _logger;
    private readonly AiCostTracker _costTracker;

    public OllamaChatService(
        IOptions<OllamaOptions> options,
        ILogger<OllamaChatService> logger,
        AiCostTracker costTracker)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _costTracker = costTracker ?? throw new ArgumentNullException(nameof(costTracker));

        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            throw new InvalidOperationException("Ollama:BaseUrl must be configured when Ai:ChatProvider is Ollama.");
        }

        // Ollama ignores the key but the SDK requires one to be present.
        var client = new OpenAIClient(
            new ApiKeyCredential("ollama"),
            new OpenAIClientOptions
            {
                Endpoint = new Uri(_options.BaseUrl),
                NetworkTimeout = TimeSpan.FromSeconds(_options.TimeoutSeconds)
            });

        _chatClient = client.GetChatClient(_options.ChatModel);

        _logger.LogInformation("OllamaChatService initialised. Endpoint: {BaseUrl}, model: {Model}",
            _options.BaseUrl, _options.ChatModel);
    }

    /// <inheritdoc />
    public async Task<StrangenessAnalysis> AnalyzeStrangenessAsync(
        List<string> reviews,
        PlaceCategory category = PlaceCategory.Restaurant,
        CancellationToken cancellationToken = default)
    {
        if (reviews == null || reviews.Count == 0)
        {
            throw new ArgumentException("Reviews list cannot be empty", nameof(reviews));
        }

        var result = await OpenAiWireChat.AnalyzeAsync(
            _chatClient,
            reviews,
            temperature: 0.3f,
            maxCompletionTokens: _options.MaxCompletionTokens,
            // A local instruct model has no reasoning budget sharing the allowance, so the
            // typed MaxOutputTokenCount property is the correct wire parameter here.
            isReasoningModel: false,
            category,
            cancellationToken);

        _costTracker.Track(ProviderLabel, _options.ChatModel, result.InputTokens, result.OutputTokens);

        return result.Analysis;
    }

}
