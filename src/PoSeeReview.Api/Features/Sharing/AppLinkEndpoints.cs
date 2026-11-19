namespace PoSeeReview.Api.Features.Sharing;

/// <summary>
/// The two files that let the native app (src/PoSeeReview.Mobile) open shared links: Android App
/// Links (<c>/.well-known/assetlinks.json</c>) and iOS Universal Links
/// (<c>/.well-known/apple-app-site-association</c>). Both claim only <c>/comic/*</c> and
/// <c>/s/*</c>, so a phone without the app still lands on the web page — that is the web fallback.
/// <para>
/// Each answers 404 until its <c>MobileApp:*</c> identity is configured: an empty fingerprint list
/// or team id would publish a claim nobody can satisfy. Neither value is a secret.
/// </para>
/// </summary>
internal static class AppLinkEndpoints
{
    private static readonly string[] ClaimedPaths = ["/comic/*", "/s/*"];
    private static readonly string[] HandleAllUrls = ["delegate_permission/common.handle_all_urls"];

    public static IEndpointRouteBuilder MapAppLinkEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/.well-known/assetlinks.json", AndroidAssetLinks)
            .WithTags("Sharing").AllowAnonymous().ExcludeFromDescription();
        app.MapGet("/.well-known/apple-app-site-association", AppleAppSiteAssociation)
            .WithTags("Sharing").AllowAnonymous().ExcludeFromDescription();
        return app;
    }

    private static IResult AndroidAssetLinks(IConfiguration config, HttpContext http)
    {
        var package = config["MobileApp:AndroidPackage"];
        var fingerprints = config.GetSection("MobileApp:AndroidCertSha256").Get<string[]>() ?? [];
        if (string.IsNullOrWhiteSpace(package) || fingerprints.Length == 0)
        {
            return Results.NotFound();
        }

        http.Response.Headers.CacheControl = "public, max-age=3600";
        return Results.Json(new[]
        {
            new
            {
                relation = HandleAllUrls,
                target = new { @namespace = "android_app", package_name = package, sha256_cert_fingerprints = fingerprints },
            },
        });
    }

    private static IResult AppleAppSiteAssociation(IConfiguration config, HttpContext http)
    {
        var appId = config["MobileApp:AppleAppId"];
        if (string.IsNullOrWhiteSpace(appId))
        {
            return Results.NotFound();
        }

        http.Response.Headers.CacheControl = "public, max-age=3600";
        return Results.Json(new
        {
            applinks = new
            {
                details = new[]
                {
                    new { appIDs = new[] { appId }, components = ClaimedPaths.Select(p => new Dictionary<string, string> { ["/"] = p }) },
                },
            },
        });
    }
}
