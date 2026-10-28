namespace PoSeeReview.Api.Features.Comics;

/// <summary>
/// Configuration options for Azure AI Foundry service (uses Azure.AI.OpenAI SDK).
/// Used for GPT-based text generation (strangeness analysis, narrative, panel dialogue).
/// Image generation is handled separately by GeminiComicService.
/// </summary>
public class AzureOpenAIOptions
{
    public const string SectionName = "AzureOpenAI";

    /// <summary>
    /// Azure AI Foundry endpoint URL for text generation (GPT models).
    /// Format: https://{resource-name}.cognitiveservices.azure.com/
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// API key for Azure AI Foundry service.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Deployment name for the GPT model. PoSeeReview uses the single shared
    /// <c>gpt-5.4-nano</c> deployment in <c>po-aiservices-shared2</c>
    /// (verified 2026-06-14 — only deployment in the resource).
    /// </summary>
    public string DeploymentName { get; set; } = "gpt-5.4-nano";

    /// <summary>
    /// Whether the configured deployment is a reasoning model.
    /// <para>
    /// This is not a flavour flag — it decides how the completion cap is put on the wire. A
    /// reasoning deployment rejects <c>max_tokens</c> with HTTP 400 <c>unsupported_parameter</c>
    /// and requires <c>max_completion_tokens</c>; an instruct deployment accepts the typed
    /// property the SDK emits. Set this to <c>false</c> when pointing the app at an instruct
    /// deployment such as <c>gpt-4o-mini</c>.
    /// </para>
    /// </summary>
    public bool IsReasoningModel { get; set; } = true;

    /// <summary>
    /// Maximum completion tokens for the analysis call. Null (the default) means
    /// <see cref="ChatTokenBudget.DefaultMaxCompletionTokens"/>.
    /// <para>
    /// <b>Only takes effect on an instruct deployment.</b> Reasoning deployments require the
    /// cap under a different parameter name than this SDK emits, so the setting is ignored there
    /// and startup warns when it is configured anyway. That is not a limitation worth working
    /// around: on a reasoning model the reasoning tokens are billed against the same allowance,
    /// so a cap sized for the visible JSON can be spent entirely on thinking and return an empty
    /// completion. On an instruct deployment the budget covers only the answer.
    /// </para>
    /// </summary>
    public int? MaxCompletionTokens { get; set; }

    /// <summary>
    /// gpt-image deployment used when <c>Ai:ImageProvider</c> is <c>AzureOpenAI</c>. Lives on the
    /// same resource, endpoint and key as the chat deployment.
    /// </summary>
    public string ImageDeployment { get; set; } = "gpt-image-1-mini";

    /// <summary>
    /// Set when <see cref="ImageDeployment"/> is a Black Forest Labs FLUX model (e.g.
    /// <c>flux-kontext-pro</c>): Azure serves those on <c>/providers/blackforestlabs/v1/{route}</c>,
    /// not the OpenAI images path, and they take an aspect ratio instead of a size and quality.
    /// </summary>
    public string? BlackForestLabsRoute { get; set; }

    /// <summary><c>low</c>, <c>medium</c> or <c>high</c>. Price scales with it roughly 4x per step.</summary>
    public string ImageQuality { get; set; } = "low";
}
