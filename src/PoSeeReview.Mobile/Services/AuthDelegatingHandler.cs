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
        else
        {
            var fakeUser = await sessionStore.GetFakeUserAsync().ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(fakeUser))
            {
                request.Headers.Remove("X-Fake-User");
                request.Headers.TryAddWithoutValidation("X-Fake-User", fakeUser);
            }
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
