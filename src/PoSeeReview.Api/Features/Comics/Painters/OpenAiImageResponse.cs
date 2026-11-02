using System.Text.Json;

namespace PoSeeReview.Api.Features.Comics.Painters;

/// <summary>The OpenAI images wire shape (<c>data[0].b64_json</c>), read by every OpenAI-compatible painter.</summary>
internal static class OpenAiImageResponse
{
    public static byte[] FirstImage(JsonElement root, string source) =>
        Convert.FromBase64String(root.GetProperty("data")[0].GetProperty("b64_json").GetString()
            ?? throw new InvalidOperationException($"{source} returned no image data."));
}
