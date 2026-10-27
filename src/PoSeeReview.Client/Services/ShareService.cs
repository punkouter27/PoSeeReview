using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace PoSeeReview.Client.Services;

/// <summary>How a share ended.</summary>
public enum ShareOutcome
{
    Shared,

    /// <summary>The user closed the share sheet. Nothing else happens — they just decided.</summary>
    Cancelled,

    /// <summary>No Web Share API here, so the link went to the clipboard instead.</summary>
    Copied,

    Failed
}

/// <summary>
/// The one share path: Web Share where it exists, clipboard where it does not, and the toast and
/// stinger that confirm either. The comic page and the Hall of Fame both go through it.
/// </summary>
public sealed class ShareService(
    IJSRuntime js,
    ApiClient api,
    NavigationManager navigation,
    ToastService toasts,
    FxService fx)
{
    /// <summary>
    /// Shares a comic by its short link. The canonical URL is only the fallback when the short
    /// one cannot be minted: a place id is thirty opaque characters, hostile in a caption.
    /// </summary>
    public async Task<ShareOutcome> ShareComicAsync(string placeId, string restaurantName, int score)
    {
        var url = (await api.CreateShareLinkAsync(placeId))?.ShortUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            url = navigation.ToAbsoluteUri($"/comic/{placeId}").ToString();
        }

        return await ShareAsync(
            restaurantName,
            $"{restaurantName} scored {score}/100 on the strangeness scale. Look at this.",
            url,
            placeId);
    }

    /// <param name="placeId">When given, the comic image is attached where the browser can share files.</param>
    public async Task<ShareOutcome> ShareAsync(string title, string text, string url, string? placeId = null)
    {
        var outcome = await InvokeShareAsync(title, text, url, placeId);

        switch (outcome)
        {
            case ShareOutcome.Shared:
                toasts.Show("Shared!");
                await fx.PlayShareStingerAsync();
                break;
            case ShareOutcome.Copied:
                toasts.Show("Link copied to clipboard");
                await fx.PlayShareStingerAsync();
                break;
            case ShareOutcome.Failed:
                toasts.Show("Couldn't share that one.");
                await fx.PlayErrorAsync();
                break;
        }

        return outcome;
    }

    private async Task<ShareOutcome> InvokeShareAsync(string title, string text, string url, string? placeId)
    {
        try
        {
            switch (await js.InvokeAsync<string>("shareUtils.share", title, text, url, placeId))
            {
                case "shared": return ShareOutcome.Shared;
                case "cancelled": return ShareOutcome.Cancelled;
            }
        }
        catch (JSException)
        {
            // Same as "unsupported": fall through to the clipboard.
        }

        try
        {
            await js.InvokeVoidAsync("shareUtils.copyToClipboard", url);
            return ShareOutcome.Copied;
        }
        catch (JSException)
        {
            return ShareOutcome.Failed;
        }
    }
}
