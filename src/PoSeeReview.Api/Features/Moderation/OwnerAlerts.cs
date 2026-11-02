using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using PoSeeReview.Shared.Ids;

namespace PoSeeReview.Api.Features.Moderation;

/// <summary>
/// A push to the owner's phone (ntfy) whenever something was kept off the app or edited before
/// it got there: an auto-hide, a content-screen block or flag, an image-model refusal, a softened
/// prompt. The owner asked to know every time; the moderation queue is where to act on it.
/// <para>
/// Fire-and-forget and never throws: a notification is a courtesy on top of a pipeline that has
/// already decided. Published as JSON to the server root, so titles with accented restaurant
/// names survive (ntfy headers are ASCII-only).
/// </para>
/// </summary>
public sealed class OwnerAlerts(
    IHttpClientFactory httpClientFactory,
    IOptions<ModerationOptions> options,
    ILogger<OwnerAlerts> logger)
{
    public const string HttpClientName = "OwnerAlerts";

    public bool Enabled => !string.IsNullOrWhiteSpace(options.Value.AlertNtfyTopic);

    /// <param name="title">What happened, e.g. "Image model refused".</param>
    /// <param name="message">The detail: the category, the reason, the words.</param>
    /// <param name="placeId">The comic it happened to, for the tap-through link.</param>
    /// <param name="urgent">Hidden or blocked (something did not publish) rather than a heads-up.</param>
    public void Send(string title, string message, PlaceId? placeId = null, bool urgent = false)
    {
        if (!Enabled)
        {
            return;
        }

        _ = SendAsync(title, message, placeId, urgent);
    }

    private async Task SendAsync(string title, string message, PlaceId? placeId, bool urgent)
    {
        var settings = options.Value;
        var linkBase = settings.AlertLinkBase.TrimEnd('/');
        string? click = linkBase.Length == 0 ? null
            : placeId is { } place ? $"{linkBase}/comic/{Uri.EscapeDataString(place.Value)}"
            : $"{linkBase}/moderation";

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.PostAsJsonAsync(settings.AlertNtfyServer.TrimEnd('/'), new
            {
                topic = settings.AlertNtfyTopic,
                title = Truncate(title, 120),
                message = Truncate(placeId is { } id ? $"{message}\n{id.Value}" : message, 1000),
                tags = new[] { urgent ? "no_entry" : "warning" },
                priority = urgent ? 4 : 3,
                click
            });

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Owner alert was not accepted by ntfy: {Status}", (int)response.StatusCode);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Owner alert could not be sent");
        }
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
