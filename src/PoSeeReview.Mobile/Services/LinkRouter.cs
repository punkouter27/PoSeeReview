namespace PoSeeReview.Mobile.Services;

/// <summary>
/// Turns an incoming link into a comic route. Accepted: <c>https://{host}/comic/{placeId}</c>,
/// <c>https://{host}/s/{code}</c> (resolved by the comic page), and <c>poseereview://comic/{placeId}</c>.
/// </summary>
public static class LinkRouter
{
    public static string? ToRoute(Uri uri)
    {
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (uri.Scheme == MobileConfig.AppScheme && uri.Host == "comic" && segments.Length == 1)
        {
            return Route("placeId", segments[0]);
        }

        if (uri.Scheme != Uri.UriSchemeHttps || segments.Length != 2)
        {
            return null;
        }

        return segments[0] switch
        {
            "comic" => Route("placeId", segments[1]),
            "s" => Route("code", segments[1]),
            _ => null,
        };
    }

    private static string Route(string key, string value)
        => $"comic?{key}={Uri.EscapeDataString(Uri.UnescapeDataString(value))}";
}
