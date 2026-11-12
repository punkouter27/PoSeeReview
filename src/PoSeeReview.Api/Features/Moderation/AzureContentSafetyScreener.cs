using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PoSeeReview.Shared.Contracts;

namespace PoSeeReview.Api.Features.Moderation;

/// <summary>
/// Azure AI Content Safety under the word lists.
/// <para>
/// The lexical screen runs first and keeps the last word on what it knows — its allegation and
/// prohibited lists are product rules, not a classifier's opinion. The classifier adds what word
/// lists cannot see: hateful or sexual content phrased without a listed word. Severity maps onto
/// the same grades: a high severity in a category with no legitimate reading in a joke comic
/// blocks, anything above "safe" flags for a human, and cartoon violence only flags when severe.
/// </para>
/// <para>
/// <b>Fails open to the lexical verdict.</b> Same reasoning as the moderation gate: an outage of an
/// advisory classifier must not stop every comic.
/// </para>
/// </summary>
public sealed class AzureContentSafetyScreener(
    LexicalContentSafetyScreener lexical,
    IHttpClientFactory httpClientFactory,
    IOptions<ModerationOptions> options,
    IConfiguration configuration,
    ILogger<AzureContentSafetyScreener> logger) : IContentSafetyScreener
{
    public const string HttpClientName = "ContentSafety";

    private const string ApiVersion = "2024-09-01";
    private static readonly string[] ModerationCategories = ["Hate", "Sexual", "Violence", "SelfHarm"];

    public async Task<ContentScreenResult> ScreenAsync(string text, CancellationToken cancellationToken = default)
    {
        var lexicalResult = await lexical.ScreenAsync(text, cancellationToken);
        var endpoint = options.Value.ContentSafetyEndpoint;
        var key = configuration["AzureOpenAI:ApiKey"];

        if (lexicalResult.IsBlocked || string.IsNullOrWhiteSpace(text)
            || string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(key))
        {
            return lexicalResult;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"{endpoint.TrimEnd('/')}/contentsafety/text:analyze?api-version={ApiVersion}");
            request.Headers.Add("Ocp-Apim-Subscription-Key", key);
            request.Content = JsonContent.Create(new
            {
                text = text.Length > 10_000 ? text[..10_000] : text,
                categories = ModerationCategories,
                outputType = "FourSeverityLevels"
            });

            using var response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var severities = json.RootElement.GetProperty("categoriesAnalysis").EnumerateArray()
                .ToDictionary(c => c.GetProperty("category").GetString() ?? "", c => c.GetProperty("severity").GetInt32());

            return Grade(severities) ?? lexicalResult;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Content Safety unavailable; using the lexical verdict");
            return lexicalResult;
        }
    }

    /// <summary>Null when the classifier sees nothing, so the lexical verdict stands.</summary>
    internal static ContentScreenResult? Grade(IReadOnlyDictionary<string, int> severities)
    {
        int Of(string category) => severities.TryGetValue(category, out var s) ? s : 0;

        // Only the top severity (6) refuses; everything from 4 publishes and reaches the owner.
        // The owner wants the app as uncensored as possible and told about every call it makes.
        foreach (var category in new[] { "Sexual", "Hate", "SelfHarm" })
        {
            if (Of(category) >= 6)
            {
                return new ContentScreenResult(ContentScreenOutcome.Block, $"cs-{category.ToLowerInvariant()}");
            }
        }

        foreach (var category in new[] { "Sexual", "Hate", "SelfHarm" })
        {
            if (Of(category) >= 4)
            {
                return new ContentScreenResult(ContentScreenOutcome.Flag, $"cs-{category.ToLowerInvariant()}");
            }
        }

        return Of("Violence") >= 4 ? new ContentScreenResult(ContentScreenOutcome.Flag, "cs-violence") : null;
    }
}
