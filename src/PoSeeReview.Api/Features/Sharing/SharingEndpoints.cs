using PoSeeReview.Api.Middleware;
using PoSeeReview.Shared.Contracts;
using PoSeeReview.Shared.Dtos;
using PoSeeReview.Shared.Ids;

namespace PoSeeReview.Api.Features.Sharing;

/// <summary>
/// Sharing slice. Maps the link-preview card <c>/share/{placeId}/card.png</c>, short links under
/// <c>/api/share</c> and the public <c>/s/{code}</c> resolver (NET_RULES 3.3). Reads comics only
/// through the Shared contracts.
/// <para>
/// A comic's canonical URL carries a Google place id — around thirty opaque characters. That is
/// fine in an href and hostile everywhere a link is actually shared: a message, a caption, a
/// screenshot someone retypes. The short form is also the only stable handle this app has for
/// counting how a comic travelled.
/// </para>
/// </summary>
internal static class SharingEndpoints
{
    /// <summary>Public prefix for a minted link. Short on purpose — it is half the point.</summary>
    public const string ResolverPrefix = "/s";

    public static IEndpointRouteBuilder MapSharingEndpoints(this IEndpointRouteBuilder app)
    {
        // The link-preview card. Deliberately NOT under /api: UserAgentValidationMiddleware only
        // lets social crawlers through on non-/api paths, so an og:image under /api would be
        // fetched by exactly the clients that get a 400 there. Anonymous for the same reason —
        // a 401 on a preview fetch unfurls as the same blank card as no tags at all.
        // Output-cached because each one is an anonymous blob download plus a Skia render.
        // The shared policy keeps an entry for 60 seconds, which is also the longest a card for a
        // comic that has since been withheld can still be served from this cache.
        app.MapGet("/share/{placeId}/card.png", GetShareCardAsync)
            .WithTags("Sharing")
            .AllowAnonymous()
            .RequireValidPlaceId()
            .CacheOutput(SharedReadCachePolicy.Name);

        var group = app.MapGroup("/api/share").WithTags("ShareLinks").RequireValidPlaceId();

        // Minting requires a session, like every other business slice: it writes a row.
        group.MapPost("/{placeId}", CreateAsync);

        // Resolving does not. This is the address people paste into messages, and 401-ing a
        // shared link would make the feature pointless. It sits outside /api deliberately —
        // UserAgentValidationMiddleware only lets link-preview crawlers through on non-/api
        // paths, and a short link that unfurls as a 400 is worse than no short link.
        app.MapGet($"{ResolverPrefix}/{{code}}", ResolveAsync)
            .WithTags("ShareLinks")
            .AllowAnonymous();

        return app;
    }

    /// <summary>
    /// Renders the 1200x630 image a shared link unfurls as.
    /// <para>
    /// <c>og:image</c> used to point at the comic's blob URL directly, and that URL carries a SAS
    /// signature that lapses in about a week. Every share older than the signature unfurled as a
    /// blank card, on a page whose Hall of Fame row is designed to last forever.
    /// </para>
    /// <para>
    /// A withheld or missing comic is a plain 404, not a 451: this is a crawler-facing image, a
    /// withheld comic should simply produce no card, and a legal-reasons status inside an img tag
    /// is noise nobody reads.
    /// </para>
    /// </summary>
    private static async Task<IResult> GetShareCardAsync(
        string placeId,
        ILiveComicLookup liveComics,
        IContentModerationGate moderationGate,
        ShareCardService shareCardService,
        HttpContext http)
    {
        var id = PlaceId.From(placeId);
        var comic = (await moderationGate.EvaluateAsync(id, http.RequestAborted)).IsServable
            ? await liveComics.GetLiveAsync(id)
            : null;

        if (comic is null || await shareCardService.RenderAsync(comic, http.RequestAborted) is not { } png)
        {
            return Results.NotFound();
        }

        // Long enough that a crawler re-fetching a popular link does not re-render it, short
        // enough that a takedown stops being served within the hour. The card is composed
        // from a comic, so it must not outlive one by much.
        http.Response.Headers.CacheControl = "public, max-age=3600";

        return Results.File(png, "image/png");
    }

    private static async Task<IResult> CreateAsync(
        string placeId,
        ShareLinkRepository repository,
        HttpContext http)
    {
        var origin = $"{http.Request.Scheme}://{http.Request.Host}";
        var code = await repository.GetOrCreateCodeAsync(PlaceId.From(placeId), http.RequestAborted);

        return Results.Ok(new ShareLinkDto
        {
            Code = code,
            ShortUrl = $"{origin}{ResolverPrefix}/{code}",
            CanonicalUrl = $"{origin}/comic/{Uri.EscapeDataString(placeId)}"
        });
    }

    /// <summary>
    /// Sends a short link on to the comic page.
    /// <para>
    /// 302, not 301. A permanent redirect is cached by the browser and by every intermediary,
    /// which would outlive a takedown: the comic would be gone and the hop would still be
    /// burned into the client. The whole point of the moderation path is that content can stop
    /// being reachable.
    /// </para>
    /// </summary>
    private static async Task<IResult> ResolveAsync(
        string code,
        ShareLinkRepository repository,
        HttpContext http)
    {
        var placeId = await repository.ResolveAsync(code, http.RequestAborted);

        if (placeId is null)
        {
            // Straight to the app rather than a 404 document. Someone following a dead short
            // link is a person holding a bad address, and the useful thing to show them is the
            // app, not an error page they cannot act on.
            return Results.Redirect("/", permanent: false);
        }

        return Results.Redirect($"/comic/{Uri.EscapeDataString(placeId.Value.Value)}", permanent: false);
    }
}
