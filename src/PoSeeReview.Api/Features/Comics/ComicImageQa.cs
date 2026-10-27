using PoSeeReview.Api.Telemetry;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Azure.AI.OpenAI;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Options;
using OpenAI.Chat;
using PoSeeReview.Shared.Ids;

namespace PoSeeReview.Api.Features.Comics;

/// <summary><c>ImageQa</c> configuration: how often, and with which deployment, drawn comics are checked.</summary>
public sealed class ImageQaOptions
{
    public const string SectionName = "ImageQa";

    /// <summary>Fraction of freshly drawn comics checked, 0-1. 0 turns the check off.</summary>
    public double SampleRate { get; set; }

    /// <summary>A vision-capable chat deployment on the scorer's resource.</summary>
    public string Deployment { get; set; } = "gpt-5.4-nano";
}

/// <summary>One drawn comic waiting for a quality check.</summary>
public sealed record ComicImageQaRequest(
    PlaceId PlaceId, byte[] Image, IReadOnlyList<string> Scenes, IReadOnlyList<string> Captions, string ImageModel);

/// <summary>
/// The hand-off between the generation pipeline and <see cref="ComicImageQaService"/>. Bounded and
/// lossy on purpose: the check is a measurement, and a burst of comics must never queue memory or
/// slow a request down — a dropped sample costs nothing but a data point.
/// </summary>
public sealed class ComicImageQaQueue(IOptions<ImageQaOptions> options)
{
    private readonly Channel<ComicImageQaRequest> _channel = Channel.CreateBounded<ComicImageQaRequest>(
        new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    public ChannelReader<ComicImageQaRequest> Reader => _channel.Reader;

    public bool TryEnqueue(ComicImageQaRequest request) =>
        Random.Shared.NextDouble() < options.Value.SampleRate && _channel.Writer.TryWrite(request);
}

/// <summary>
/// Asks a vision model whether a drawn comic matches what was ordered: the right number of
/// panels, no lettering the painter invented, and scenes that show what the shot list says.
/// <para>
/// <b>It measures; it never redraws.</b> A failed check is a metric dimensioned by image model —
/// the evidence for choosing a draft painter — not a reason to pay for the same comic twice.
/// </para>
/// </summary>
public sealed class ComicImageQaService(
    ComicImageQaQueue queue,
    AzureOpenAIClient client,
    IOptions<ImageQaOptions> options,
    AiCostTracker costTracker,
    TelemetryClient telemetry,
    ILogger<ComicImageQaService> logger) : BackgroundService
{
    internal const string SystemMessage = """
You check a generated comic strip against the brief it was drawn from. The yellow caption boxes were added afterwards and are expected; ignore them.
Report, as JSON:
- panelCount: how many separate panels the artwork shows.
- strayText: true if the artwork contains lettering, words or fake text anywhere other than the yellow caption boxes.
- sceneMatch: 0-5, how faithfully the panels show the numbered scenes (5 = every scene clearly drawn, 0 = unrelated).
- consistentCharacters: true if a character who appears in more than one panel looks the same in each.
""";

    private static readonly BinaryData Schema = BinaryData.FromString("""
{
  "type": "object",
  "additionalProperties": false,
  "required": ["panelCount", "strayText", "sceneMatch", "consistentCharacters"],
  "properties": {
    "panelCount": { "type": "integer" },
    "strayText": { "type": "boolean" },
    "sceneMatch": { "type": "integer" },
    "consistentCharacters": { "type": "boolean" }
  }
}
""");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var request in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await CheckAsync(request, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Image QA failed for {PlaceId}", request.PlaceId.Value);
            }
        }
    }

    private async Task CheckAsync(ComicImageQaRequest request, CancellationToken cancellationToken)
    {
        var deployment = options.Value.Deployment;
        var brief = string.Join("\n", request.Scenes.Select((scene, i) => $"{i + 1}. {scene}"));

        ChatMessage[] messages =
        [
            new SystemChatMessage(SystemMessage),
            new UserChatMessage(
                ChatMessageContentPart.CreateTextPart($"Panels ordered: {request.Scenes.Count}\nScenes:\n{brief}"),
                // Low detail is a fixed ~85 tokens: enough to count panels and spot lettering.
                ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(request.Image), "image/webp", ChatImageDetailLevel.Low))
        ];

        var completion = (await client.GetChatClient(deployment).CompleteChatAsync(messages, new ChatCompletionOptions
        {
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat("comic_qa", Schema, jsonSchemaIsStrict: true)
        }, cancellationToken)).Value;

        costTracker.Track(AzureOpenAIChatService.ProviderLabel, deployment,
            completion.Usage?.InputTokenCount ?? 0, completion.Usage?.OutputTokenCount ?? 0,
            completion.Usage?.InputTokenDetails?.CachedTokenCount ?? 0);

        if (completion.Content.Count == 0
            || JsonSerializer.Deserialize<QaVerdict>(completion.Content[0].Text) is not { } verdict)
        {
            return;
        }

        var model = string.IsNullOrWhiteSpace(request.ImageModel) ? "unknown" : request.ImageModel;
        telemetry.GetMetric("Comics.ImageQa.PanelCountOk", "ImageModel").TrackValue(verdict.PanelCount == request.Scenes.Count ? 1 : 0, model);
        telemetry.GetMetric("Comics.ImageQa.StrayText", "ImageModel").TrackValue(verdict.StrayText ? 1 : 0, model);
        telemetry.GetMetric("Comics.ImageQa.SceneMatch", "ImageModel").TrackValue(Math.Clamp(verdict.SceneMatch, 0, 5), model);
        telemetry.GetMetric("Comics.ImageQa.ConsistentCharacters", "ImageModel").TrackValue(verdict.ConsistentCharacters ? 1 : 0, model);

        logger.LogInformation(
            "Image QA {PlaceId} ({Model}): panels {Panels}/{Ordered}, stray text {StrayText}, scene match {SceneMatch}/5, consistent {Consistent}",
            request.PlaceId.Value, model, verdict.PanelCount, request.Scenes.Count, verdict.StrayText, verdict.SceneMatch, verdict.ConsistentCharacters);
    }

    private sealed record QaVerdict(
        [property: JsonPropertyName("panelCount")] int PanelCount,
        [property: JsonPropertyName("strayText")] bool StrayText,
        [property: JsonPropertyName("sceneMatch")] int SceneMatch,
        [property: JsonPropertyName("consistentCharacters")] bool ConsistentCharacters);
}
