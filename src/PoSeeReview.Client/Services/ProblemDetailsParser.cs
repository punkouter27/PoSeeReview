using System.Text.Json;

namespace PoSeeReview.Client.Services;

/// <summary>Parses RFC 7807 ProblemDetails messages from API failure responses.</summary>
public static class ProblemDetailsParser
{
    public static string? ExtractMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("detail", out var detail)
                && detail.ValueKind == JsonValueKind.String
                && detail.GetString() is { Length: > 0 } detailText)
            {
                return detailText;
            }

            if (doc.RootElement.TryGetProperty("title", out var title)
                && title.ValueKind == JsonValueKind.String
                && title.GetString() is { Length: > 0 } titleText)
            {
                return titleText;
            }
        }
        catch (JsonException)
        {
            // Fall through when payload is not JSON.
        }

        return null;
    }
}
