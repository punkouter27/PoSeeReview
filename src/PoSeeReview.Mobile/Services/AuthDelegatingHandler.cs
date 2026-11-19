using System.Net.Http.Headers;

namespace PoSeeReview.Mobile.Services;

public sealed class AuthDelegatingHandler(SecureSessionStore sessionStore) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await sessionStore.GetBearerTokenAsync().ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
#if DEBUG
        else
        {
            // The API's Dev/Test-only guest identity; Release builds never send it.
            var fakeUser = await sessionStore.GetFakeUserAsync().ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(fakeUser))
            {
                request.Headers.Remove("X-Fake-User");
                request.Headers.TryAddWithoutValidation("X-Fake-User", fakeUser);
            }
        }
#endif

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
